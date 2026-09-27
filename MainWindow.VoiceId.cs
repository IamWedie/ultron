// MainWindow.VoiceId.cs - voice identity: capture, STT pump, setup
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
    /* ===================== VOICE ID ===================== */

    private void StartMonitor()
    {
        if (_audio is null) return;
        if (!_audio.IsActive)
        {
            var err = _audio.Start();
            if (err.Length > 0) AddMessage("system", err);
        }
    }

    private void StopMonitor()
    {
        if (_audio is not null && _audio.IsActive && _captureSink is null && _verifyBuffer is null)
            _audio.Stop();
    }

    private void OnWakeSpotted()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_geminiMode)
            {
                // Live mode: "Hey Ultron" heard on the local wake monitor —
                // flip the backend awake so Gemini starts hearing us.
                if (_gemini.IsConnected && _wakeGateOn && !_awakeInGemini)
                {
                    _lastInteraction = DateTime.UtcNow;
                    _awakeInGemini = true;
                    SetMicVisual(true);
                    AddMessage("system", "WAKE WORD — I'm listening.");
                    _ = _gemini.SetAwakeAsync(true);
                }
                return;
            }
            _lastInteraction = DateTime.UtcNow;
            if (_sm.Current is AssistantState.Sleep or AssistantState.Rest)
            {
                AddMessage("system", "WAKE WORD — verifying owner.");
                StartWakeVerify();
            }
        });
    }

    private void StartWakeVerify()
    {
        if (_geminiMode) return; // cannot capture mic for verify while Gemini streams it
        _sm.WakeDetected();
        _verifyBuffer = new List<float>();
        _verifyStart = DateTime.UtcNow;
        _silentTicks = 0;
        _verifyWatch.Start();
        SetMicVisual(true);
        SetConfidence(0f);
    }

    private void CheckVerifyProgress()
    {
        float[]? snap = null;
        lock (_verifyLock)
        {
            if (_verifyBuffer is null) return;
            snap = _verifyBuffer.ToArray();
        }
        var n = snap.Length;
        if (_sm.Current != AssistantState.WakeListening)
        {
            _verifyWatch.Stop();
            lock (_verifyLock) _verifyBuffer = null;
            SetMicVisual(false);
            return;
        }
        var tail = VoiceAudio.SampleRate / 5;
        if (n >= tail)
        {
            double sum = 0;
            for (var i = n - tail; i < n; i++)
            {
                var v = snap[i];
                sum += v * v;
            }
            if (Math.Sqrt(sum / tail) < 0.006) _silentTicks++;
            else _silentTicks = 0;
        }
        if (_silentTicks >= 3 || (DateTime.UtcNow - _verifyStart).TotalSeconds >= 4.0)
            FinalizeVerify();
    }

    private void FinalizeVerify()
    {
        _verifyWatch.Stop();
        float[] pcm;
        lock (_verifyLock)
        {
            pcm = (_verifyBuffer ?? new List<float>()).ToArray();
            _verifyBuffer = null;
        }
        SetMicVisual(false);
        if (_sm.Current != AssistantState.WakeListening) return;
        var score = _voiceId.LastOwnerScore(pcm);
        var ok = _voiceId.Verify(pcm);
        Dbg($"Verify: {pcm.Length} samples, score={score:F3}, threshold={_voiceId.OwnerThreshold:F2} -> {(ok ? "PASS" : "FAIL")}");
        AddMessage("system", $"VOICE CHECK: {(score * 100):F0}% — {(ok ? "OWNER VERIFIED" : "not recognized")}.");
        SetConfidence((float)score);
        if (ok) _sm.VoiceIdPassed();
        else _sm.VoiceIdFailed();
    }

    private void SetConfidence(float score)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var s = (float)Math.Clamp(score, 0f, 1f);
            var parent = ConfidenceFill.Parent as FrameworkElement;
            var w = Math.Max(2.0, (parent?.ActualWidth ?? 200.0) * s);
            ConfidenceFill.Width = w;
            ConfidenceFill.Background = BrushFromHex(s >= _voiceId.OwnerThreshold ? "#FFFFB703" : "#FFFF2E2E");
            ConfidenceText.Text = _voiceId.OwnerEnrolled
                ? $"{(s * 100):F0}%  (threshold {(s >= _voiceId.OwnerThreshold ? "MET" : "BELOW")} {_voiceId.OwnerThreshold:P0})"
                : "no voice profile enrolled";
            _dashboard?.PushConfidence(s);
        });
    }

    private async Task<float[]> CaptureUtteranceAsync(int ms)
    {
        var dest = new List<float>();
        var started = false;
        _captureSink = dest;
        if (_audio is not null && !_audio.IsActive)
        {
            var err = _audio.Start();
            if (err.Length > 0)
            {
                lock (dest) dest.Clear();
                _captureSink = null;
                return [];
            }
            started = true;
        }
        await Task.Delay(ms);
        _captureSink = null;
        if (started && _audio is not null) _audio.Stop();
        float[] result;
        lock (dest) result = dest.ToArray();
        return result;
    }

    private async Task OpenVoiceSetupAsync()
    {
        if (_settings.PinHash.Length == 0)
        {
            AddMessage("system", "Set a guard PIN in Settings first — it protects voice-ID reset.");
            return;
        }
        var status = new TextBlock
        {
            Text = "",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 155, 161, 171)),
            TextWrapping = TextWrapping.Wrap,
        };
        var wakeCount = _voiceId.WakeEnrolled ? _voiceId.WakeEnrolledCount() : 0;
        var ownerCount = _voiceId.OwnerEnrolled ? _voiceId.OwnerEnrolledCount() : 0;
        var recWake = new Button
        {
            Content = $"Record wake phrase  ({wakeCount}/2)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var recOwner = new Button
        {
            Content = $"Record passphrase  ({ownerCount}/3)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0),
        };
        recWake.Click += async (_, _) =>
        {
            recWake.IsEnabled = false;
            status.Text = "Saying \"Hey Ultron\" — recording 2.5s...";
            var idx = _voiceId.WakeEnrolledCount();
            var pcm = await CaptureUtteranceAsync(2500);
            if (pcm.Length == 0) { status.Text = "No audio captured."; }
            else
            {
                var score = _voiceId.AddWakeSample(pcm);
                status.Text = score < 0
                    ? "Unclear audio — repeat the wake phrase."
                    : $"Wake sample {idx + 1}/2 added (match {score:F2}).";
            }
            recWake.Content = $"Record wake phrase  ({_voiceId.WakeEnrolledCount()}/2)";
            recWake.IsEnabled = true;
        };
        recOwner.Click += async (_, _) =>
        {
            recOwner.IsEnabled = false;
            status.Text = "Saying your passphrase — recording 2.5s...";
            var idx = _voiceId.OwnerEnrolledCount();
            var pcm = await CaptureUtteranceAsync(2500);
            if (pcm.Length == 0) { status.Text = "No audio captured."; }
            else
            {
                var score = _voiceId.AddOwnerSample(pcm);
                status.Text = score < 0
                    ? "Unclear audio — repeat the passphrase."
                    : $"Passphrase sample {idx + 1}/3 added (match {score:F2}).";
            }
            recOwner.Content = $"Record passphrase  ({_voiceId.OwnerEnrolledCount()}/3)";
            recOwner.IsEnabled = true;
        };
        var pinBox = new PasswordBox { PlaceholderText = "Guard PIN (required to save/reset)" };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "VOICE ID SETUP",
            Content = new StackPanel
            {
                Spacing = 6,
                Width = 430,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Record the wake word \"Hey Ultron\" twice, then your passphrase " +
                               "\"Ultron, it's me\" three times. Speak naturally; keep 2m away from noise.",
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 155, 161, 171)),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    recWake,
                    recOwner,
                    status,
                    pinBox,
                },
            },
            PrimaryButtonText = "Save & Arm",
            CloseButtonText = "Cancel",
        };
        dialog.PrimaryButtonClick += (_, ea) =>
        {
            if (!_settings.VerifyPin(pinBox.Password))
            {
                status.Text = "Wrong guard PIN.";
                ea.Cancel = true;
                return;
            }
            if (!_voiceId.WakeEnrolled || !_voiceId.OwnerEnrolled)
            {
                status.Text = "Record at least 1 wake sample and 1 passphrase sample first.";
                ea.Cancel = true;
                return;
            }
            _settings.VoiceProfile = _voiceId.ToProfile();
            _settings.VoiceEnrolled = true;
            _settings.Save();
        };
        StopMonitor();
        await dialog.ShowAsync();
        if (_voiceId.WakeEnrolled)
        {
            StartMonitor();
            AddMessage("system", "VOICE ID armed — wake-word monitor active.");
        }
    }

    private void CommandInput_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) SendCommand();
    }

    private async void SendCommand()
    {
        var text = CommandInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        CommandInput.Text = "";
        _lastInteraction = DateTime.UtcNow;
        AddMessage("user", text);
        _sm.EngageFromText();

        if (_geminiMode && _gemini.IsConnected)
        {
            // Gemini Live path: send text; the spoken/text reply arrives async
            // via the TranscriptReceived event.
            try { await _gemini.SendTextAsync(text); }
            catch (Exception ex) { AddMessage("system", "Gemini: " + ex.Message); }
            return;
        }

        var replyBox = AddMessage("ultron", "");
        try
        {
            var reply = await _brain.AskAsync(text, chunk =>
            {
                if (replyBox.Children[1] is TextBlock tb)
                {
                    DispatcherQueue.TryEnqueue(() => tb.Text += chunk);
                }
            });
            if (replyBox.Children[1] is TextBlock tb2 && string.IsNullOrEmpty(tb2.Text))
            {
                tb2.Text = reply;
            }
            _sm.CommandFinished();
        }
        catch (Exception ex)
        {
            AddMessage("system", ex.Message);
            _sm.CommandFinished();
        }
    }

}
