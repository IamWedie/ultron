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
        if (_micMuted)
        {
            AddMessage("system", "Mic is muted — press Win+Alt+M to unmute.");
            return;
        }
        ToggleAwake();
    }

    private void TogglePtt()
    {
        _lastInteraction = DateTime.UtcNow;
        if (_micMuted) return;
        DispatcherQueue.TryEnqueue(() => ToggleAwake());
    }

    /// <summary>
    /// Flips the microphone gate — audio streaming to Gemini on or off. This is
    /// NOT the "Hey Ultron" gate: that one is a system-prompt instruction armed by
    /// <see cref="_wakeGateOn"/> and only takes effect while the mic is already on.
    ///
    /// The backend is the source of truth for whether the mic is actually open.
    /// <see cref="_awakeInGemini"/> only remembers the last state the backend
    /// reported, so the click inverts that rather than guessing. Painting happens
    /// in the status handler, which means the idle timeout and go_to_sleep land on
    /// the UI immediately instead of waiting for a click to resync it.
    /// </summary>
    private void ToggleAwake()
    {
        _lastInteraction = DateTime.UtcNow;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_gemini.IsConnected || _gemini.State == "connecting")
            {
                // Speech is cloud-only, so there is no local gate to flip. Say so
                // rather than pretending: a toggle that silently does nothing is
                // exactly the failure that made the wake control untrustworthy.
                // "connecting" counts as not-ready on purpose — the process is up
                // but no Gemini session is attached yet, so flipping the gate now
                // would paint red while nothing is listening.
                AddMessage("system", _gemini.IsConnected
                    ? "Gemini Live is still connecting — the mic toggle does nothing until the session is live."
                    : "Gemini Live is not connected — speech is unavailable. Reconnecting; you can type meanwhile.");
                SetMicToggleVisual(false);
                SetMicVisual(false);
                return;
            }
            bool next = !_awakeInGemini;
            // A conversation continues until the mic is turned off, the model is
            // told to sleep, or the room goes quiet past the idle timeout.
            AddMessage("system", next
                ? (_wakeGateOn
                    ? "Mic on — say \u201cHey Ultron\u201d to start."
                    : "Mic on — listening.")
                : "Mic off — only typed messages until you turn it on again.");
            _ = _gemini.SetAwakeAsync(next);
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
