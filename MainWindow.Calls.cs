// MainWindow.Calls.cs - Telegram call state and call controls
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
    private void ScheduleShutdown(int afterSeconds)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(afterSeconds * 1000);
            DispatcherQueue.TryEnqueue(() => Application.Current.Exit());
        });
    }

    private void OnGeminiDisconnected()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AddMessage("system", "Gemini disconnected — attempting reconnect in 5s...");
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                if (!string.IsNullOrEmpty(_settings.GeminiApiKey))
                {
                    await _gemini.StartAsync(_settings.GeminiApiKey, _settings.GeminiVoice);
                    if (_micMuted) await _gemini.SetMicMutedAsync(true);
                }
            });
        });
    }

    private CallWindow? _callWindow;
    private bool _callWindowShown;

    /// <summary>Lazily creates the call window. Created on first ring rather than
    /// at startup so a normal session does not pay for an always-on-top window
    /// nobody is looking at.</summary>
    private CallWindow EnsureCallWindow()
    {
        if (_callWindow is not null) return _callWindow;

        _callWindow = new CallWindow();
        _callWindow.EndCallRequested += () =>
        {
            // The user closed the window as a way to hang up. Tell the backend
            // once; the terminal state that follows closes it again, and a second
            // stop would be a second "hang up" for a call that is already gone.
            _callWindowShown = false;
            _ = _gemini.SendTelegramCallStopAsync();
        };
        _callWindow.AcceptCallRequested += () =>
            AddMessage("system", "Accepting an incoming call is not wired up yet: ULTRON only places outgoing calls.");
        _callWindow.MuteToggledRequested += ToggleMicMute;
        _callWindow.SetMuted(_micMuted);
        _callWindow.ApplyIdle();
        return _callWindow;
    }

    private void HandleCallStateChanged(string state, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var window = EnsureCallWindow();
                if (CallStateRules.TryExtractTarget(state, message, out var target))
                    window.SetTarget(target);

                // A new outgoing call starts from zero; a stale total from the
                // last one would make the timer read like the call already ran.
                if (CallStateRules.StartsNewCall(state)) _callDuration = TimeSpan.Zero;

                window.ApplyState(state, message);
                var finished = CallStateRules.IsTerminal(state);

                // Only a real, answered call runs the duration timer.
                if (CallStateRules.IsLive(state)) _callTimer?.Start();
                else if (finished) _callTimer?.Stop();

                // A call that fails before ringing still deserves to be seen, so
                // the summary shows either way. Shown is tracked rather than
                // probed because re-showing an always-on-top window would
                // re-raise it over whatever the user moved to.
                if (!finished || !_callWindowShown)
                {
                    _callWindowShown = true;
                    window.ShowWithoutActivation();
                }
            }
            catch (Exception ex)
            {
                // A call surface must never take down the app if it fails to open.
                AppLog.Write("MainWindow", $"Call window update failed: {ex.Message}", AppLog.Level.Error);
            }
        });
    }

    /// <summary>Single owner of the call clock. The window only displays what
    /// lands here, so there is one ticker, not two.</summary>
    private void UpdateCallDuration()
    {
        _callDuration = _callDuration.Add(TimeSpan.FromSeconds(1));
        _callWindow?.SetDuration(_callDuration);
    }
}
