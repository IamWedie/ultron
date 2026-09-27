// MainWindow.LanWebDeck.cs - LAN web deck
// Part of MainWindow. Fields and composition root live in MainWindow.xaml.cs.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Ultron.Services;
using Ultron.Services.Apps;
using Ultron.Services.Processes;
using Ultron.Services.Tools.Apps;
using Windows.System;
using Windows.UI;
using Microsoft.UI;
using Xaml = Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Buffers;
using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using QRCoder;

namespace Ultron;

public sealed partial class MainWindow
{
    /* ===================== LAN WEB DECK ===================== */

    internal void EnsureDashboard(int port)
    {
        _dashboard?.Dispose();
        _dashboard = new DashboardServer(port);
        _dashboardUrl = _dashboard.Start()
            ? DashboardServer.LanUrl(_dashboard.ActualPort) + "?k=" + _dashboard.Token
            : null;
        if (_dashboardUrl is not null)
            Dbg($"web deck: {_dashboardUrl}");
        else
            Dbg($"web deck failed to start: {_dashboard.LastError}");
    }

    private void OnDashboardRequested()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_dashboardUrl is null)
            {
                _ = OpenSettingsAsync();
                return;
            }
            _tray?.ShowBalloon("ULTRON WEB DECK", _dashboardUrl);
        });
    }

    private static string DeckUrl(int port, string token) =>
        DashboardServer.LanUrl(port) + "?k=" + token;

    private WriteableBitmap BuildDeckQr(int port, string token, int px = 240)
    {
        var qr = new QRCodeGenerator();
        var data = qr.CreateQrCode(DeckUrl(port, token), QRCodeGenerator.ECCLevel.M, forceUtf8: true);
        var matrix = data.ModuleMatrix;
        var size = matrix.Count;
        var scale = Math.Max(1, px / (size + 8));
        var dim = (size + 8) * scale;
        var buf = new byte[dim * dim * 4];
        for (var y = 0; y < dim; y++)
        {
            for (var x = 0; x < dim; x++)
            {
                var m = (x / scale) - 4;
                var n = (y / scale) - 4;
                var dark = m >= 0 && n >= 0 && m < size && n < size && matrix[n][m];
                var i = (y * dim + x) * 4;
                if (dark)
                {
                    buf[i] = 0x1A; buf[i + 1] = 0x17; buf[i + 2] = 0x16; buf[i + 3] = 0xFF;
                }
                else
                {
                    buf[i] = 0xFF; buf[i + 1] = 0xFF; buf[i + 2] = 0xFF; buf[i + 3] = 0xFF;
                }
            }
        }
        var bmp = new WriteableBitmap(dim, dim);
        using (var stream = bmp.PixelBuffer.AsStream())
        {
            stream.Write(buf, 0, buf.Length);
        }
        return bmp;
    }

    private void OnHotkeyPressed(int id)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (id)
            {
                case HotkeyPtt: TogglePtt(); break;
                case HotkeyMute: ToggleMicMute(); break;
                case HotkeyWake: ToggleAwake(); break;
            }
        });
    }

    /// <summary>(Re)apply the configured global hotkeys. Reports invalid specs and
    /// combos that are already held by another app, keeping prior bindings intact.</summary>
    internal void ApplyHotkeyBindings()
    {
        if (_hotkeys is null) return;
        foreach (var (id, spec) in new[]
                 {
                     (HotkeyPtt, _settings.HotkeyPtt ?? ""),
                     (HotkeyMute, _settings.HotkeyMute ?? ""),
                     (HotkeyWake, _settings.HotkeyWake ?? ""),
                 })
        {
            if (!HotkeyService.TryParse(spec, out var mods, out var vk))
            {
                _hotkeys.Unregister(id);
                Dbg($"hotkey {id}: invalid spec '{spec}' — not registered.");
                continue;
            }
            _hotkeys.Unregister(id);
            if (_hotkeys.Register(id, mods, vk))
                Dbg($"hotkey {id}: registered '{HotkeyService.ToDisplay(mods, vk)}'.");
            else
                Dbg($"hotkey {id}: '{spec}' is already in use by another app — not registered.");
        }
    }

    private float[] CaptureSnapshot()
    {
        List<float> snap;
        lock (_speechSeg) { snap = new List<float>(_speechSeg); _speechSeg.Clear(); }
        if (snap.Count > 0) return snap.ToArray();
        if (_captureSink is not null) { lock (_captureSink) { snap = new List<float>(_captureSink); _captureSink.Clear(); } }
        return snap?.ToArray() ?? [];
    }

    private void OnAudioSamples(ReadOnlySpan<float> pcm)
    {
        if (_micMuted) return;
        if (_voiceId.WakeEnrolled) _voiceId.Feed(pcm);
        lock (_verifyLock)
        {
            if (_verifyBuffer is not null)
                foreach (var s in pcm) _verifyBuffer.Add(s);
        }
        if (_captureSink is not null)
        {
            lock (_captureSink)
                foreach (var s in pcm) _captureSink.Add(s);
        }

        if (_vad is null || !_modelsLoaded) return;
        if (_sm.Current != AssistantState.Engaged) return;

        var chunk = ArrayPool<float>.Shared.Rent(512);
        try
        {
            foreach (var sample in pcm)
            {
                _vadBuf.Add(sample);
                while (_vadBuf.Count >= 512)
                {
                    _vadBuf.CopyTo(0, chunk, 0, 512);
                    _vadBuf.RemoveRange(0, 512);
                    var prob = _vad.PredictFrame(chunk.AsSpan(0, 512));

                    if (prob >= _vad.Threshold)
                    {
                        if (!_vadSpeaking)
                        {
                            _vadSpeaking = true;
                            _vadSilenceCount = 0;
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                OrbContainer.Fill = BrushFromHex("#66FFB703");
                                TickerText.Text = "SPEECH DETECTED";
                            });
                        }
                        _vadSilenceCount = 0;
                        lock (_speechSeg)
                            for (var i = 0; i < 512; i++) _speechSeg.Add(chunk[i]);
                    }
                    else
                    {
                        if (_vadSpeaking)
                        {
                            _vadSilenceCount++;
                            if (_vadSilenceCount >= VadSilenceFrames)
                            {
                                _vadSpeaking = false;
                                _vadSilenceCount = 0;
                                ProcessSpeechSegment();
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(chunk);
        }
    }

    private void ProcessSpeechSegment()
    {
        float[] pcm;
        lock (_speechSeg)
        {
            pcm = _speechSeg.ToArray();
            _speechSeg.Clear();
        }
        if (pcm.Length < 3200) return;
        _sttQueue.Enqueue(pcm);
        _ = PumpSttAsync();
    }

    private async Task PumpSttAsync()
    {
        if (_sttPumping) return;
        _sttPumping = true;
        try
        {
            while (_sttQueue.TryDequeue(out var pcm))
            {
                string? text = null;
                try
                {
                    text = await _whisper!.TranscribeAsync(pcm);
                }
                catch (Exception ex)
                {
                    DispatcherQueue.TryEnqueue(() => AddMessage("system", "STT error: " + ex.Message));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(text)) continue;
                var t = text;
                DispatcherQueue.TryEnqueue(() => { AddMessage("user", t); _ = RunVoiceCommand(t); });
            }
        }
        finally
        {
            _sttPumping = false;
            DispatcherQueue.TryEnqueue(() => ApplyStateVisual(_sm.Current));
        }
    }

    private async Task RunVoiceCommand(string text)
    {
        Dbg($"RunVoiceCommand: '{text}'");
        if (_geminiMode && _gemini.IsConnected)
        {
            try { await _gemini.SendTextAsync(text); }
            catch (Exception ex) { Dbg($"RunVoiceCommand Gemini: {ex.Message}"); }
            _sm.CommandFinished();
            return;
        }
        _sm.CommandStarted();
            var replyBox = AddMessage("ultron", "");
            try
            {
                var reply = await _brain.AskAsync(text, chunk =>
                {
                    if (replyBox.Children[1] is TextBlock tb)
                        DispatcherQueue.TryEnqueue(() => tb.Text += chunk);
                });
                Dbg($"RunVoiceCommand: reply='{reply}'");
                if (replyBox.Children[1] is TextBlock tb2 && string.IsNullOrEmpty(tb2.Text))
                    tb2.Text = reply;
                _sm.CommandFinished();
            }
        catch (Exception ex)
        {
            Dbg($"RunVoiceCommand: FAILED {ex.Message}");
            AddMessage("system", ex.Message);
            _sm.CommandFinished();
        }
    }

    private void OnAudioLevel(float level)
    {
        if (_audio is null || !_audio.IsActive) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_sm.Current != AssistantState.Sleep)
            {
                var scale = 1.0f + Math.Clamp(level * 3.0f, 0f, 0.35f);
                OrbPulse.ScaleX = OrbPulse.ScaleY = scale;
            }
            if (level > 0.15f && _sm.Current == AssistantState.Engaged)
                _sm.CommandStarted();
        });
    }

}
