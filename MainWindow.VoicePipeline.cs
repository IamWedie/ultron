// MainWindow.VoicePipeline.cs - voice pipeline: mic capture, STT pump, command execution
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

namespace Ultron;

public sealed partial class MainWindow
{
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
