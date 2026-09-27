// MainWindow.CommandBar.cs - command bar: send, stop, attachments
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
    /* ===================== COMMAND BAR ===================== */

    internal StackPanel AddMessage(string who, string text)
    {
        var item = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };
        item.Children.Add(new TextBlock
        {
            Text = who.ToUpperInvariant(),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 10,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                who == "user"
                    ? Windows.UI.Color.FromArgb(255, 255, 179, 3)
                    : Windows.UI.Color.FromArgb(255, 255, 46, 46)),
        });
        item.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 237, 240, 245)),
            TextWrapping = TextWrapping.Wrap,
        });
        ConversationList.Items.Add(item);
        if (ConversationList.Items.Count > 200)
        {
            ConversationList.Items.RemoveAt(0);
        }
        ConversationList.ScrollIntoView(item);

        // Log to persistent memory store if enabled
        if (_settings.MemoryLogging && (who == "user" || who == "ultron"))
        {
            _ = Task.Run(async () =>
            {
                try { await _memory.LogAsync(who, text); }
                catch { /* swallow - memory logging is best-effort */ }
            });
        }

        return item;
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => SendCommand();

    private void MicButton_Click(object sender, RoutedEventArgs e)
    {
        _lastInteraction = DateTime.UtcNow;
        if (_geminiMode && _gemini.IsConnected) { ToggleAwake(); return; }
        if (_micMuted)
        {
            AddMessage("system", "Mic is muted — press Win+Alt+M to unmute.");
            return;
        }
        ToggleLocalPtt();
    }

    private void TogglePtt()
    {
        _lastInteraction = DateTime.UtcNow;
        if (_geminiMode && _gemini.IsConnected) { ToggleAwake(); return; }
        if (_micMuted) return;
        DispatcherQueue.TryEnqueue(() => ToggleLocalPtt());
    }

    private void ToggleLocalPtt()
    {
        // Manual awake/asleep override. With the wake gate armed, Gemini
        // starts asleep and only hears you after "Hey Ultron"; this button
        // forces the state either way. While Gemini talks the mic is
        // auto-muted (echo cancel), so speak after it finishes or wake it
        // again.
        if (_verifyBuffer is not null) { FinalizeVerify(); return; }
        if (_voiceId.OwnerEnrolled && _sm.Current is AssistantState.Sleep or AssistantState.Rest)
        {
            AddMessage("system", "PTT: speak your passphrase to verify.");
            StartWakeVerify();
            return;
        }
        if (_audio is not null && _audio.IsActive)
        {
            _audio.Stop();
            SetMicVisual(false);
            OrbPulse.ScaleX = OrbPulse.ScaleY = 1;
            if (_whisper is null || !_modelsLoaded) { AddMessage("system", "PTT released — models not loaded yet."); return; }
            var pcm = CaptureSnapshot();
            Dbg($"PTT release: pcm={pcm.Length}, sttQueued={_sttQueue.Count}, modelsLoaded={_modelsLoaded}");
            if (pcm.Length >= 3200)
            {
                _sttQueue.Enqueue(pcm);
                _ = PumpSttAsync();
            }
            else
                AddMessage("system", "PTT: no speech detected.");
            return;
        }
        var err = _audio?.Start() ?? "";
        if (err.Length > 0)
        {
            AddMessage("system", err);
            return;
        }
        _sm.EngageFromText();
        SetMicVisual(true);
        AddMessage("system", "PTT: listening... (speak, then release to transcribe).");
    }

    private void ToggleAwake()
    {
        _lastInteraction = DateTime.UtcNow;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_geminiMode && _gemini.IsConnected)
            {
                _awakeInGemini = !_awakeInGemini;
                SetMicVisual(_awakeInGemini);
                AddMessage("system", _awakeInGemini
                    ? "AWAKE — I'm listening (one command per wake)."
                    : "ASLEEP — press the mic or say \u201cHey Ultron\u201d to wake me.");
                _ = _gemini.SetAwakeAsync(_awakeInGemini);
                return;
            }
            if (_voiceId.OwnerEnrolled && _settings.VoiceEnrolled &&
                _sm.Current is AssistantState.Sleep or AssistantState.Rest)
            {
                AddMessage("system", "Wake toggle — verifying owner.");
                StartWakeVerify();
            }
            else
            {
                AddMessage("system", "Wake toggle — already awake.");
            }
        });
    }

    private void ToggleMicMute()
    {
        _micMuted = !_micMuted;
        _settings.MicMuted = _micMuted;
        try { _settings.Save(); } catch (Exception ex) { AppLog.Write("MainWindow", $"Settings save failed: {ex.Message}", AppLog.Level.Error); }
        _ = _gemini.SetMicMutedAsync(_micMuted);
        DispatcherQueue.TryEnqueue(() =>
        {
            SetMicVisual(false);
            var hotkey = HotkeyService.TryParse(_settings.HotkeyMute, out var m, out var v)
                ? HotkeyService.ToDisplay(m, v)
                : "Win+Alt+U";
            TickerText.Text = _micMuted ? $"MIC MUTED — {hotkey} to unmute." : "MIC UNMUTED";
            AddMessage("system", _micMuted ? "Mic muted — I can't hear you." : "Mic unmuted — listening.");
            _notify?.Notify("ULTRON", _micMuted
                ? "Microphone muted."
                : "Microphone unmuted.");
        });
    }

    private void ShowFromTray()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized)
                p.Restore();
            _appWindow.Show();
            Activate();
        });
    }

}
