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

    private void HandleCallStateChanged(string state, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (CallStatusOverlay is null) return;

            switch (state.ToLowerInvariant())
            {
                case "ringing":
                    CallStatusOverlay.Visibility = Visibility.Visible;
                    CallStatusText.Text = "RINGING";
                    CallTargetText.Text = message;
                    CallDurationText.Text = "00:00";
                    _callDuration = TimeSpan.Zero;
                    CallStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Orange);
                    CallHangupButton.Visibility = Visibility.Visible;
                    CallAcceptButton.Visibility = Visibility.Collapsed;
                    CallDeclineButton.Visibility = Visibility.Collapsed;
                    _callTimer?.Start();
                    break;

                case "connecting":
                    CallStatusText.Text = "CONNECTING";
                    CallTargetText.Text = message;
                    CallStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Orange);
                    break;

                case "connected":
                    CallStatusText.Text = "CONNECTED";
                    CallTargetText.Text = message;
                    CallStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
                    CallAcceptButton.Visibility = Visibility.Collapsed;
                    CallDeclineButton.Visibility = Visibility.Collapsed;
                    break;

                case "remote_speech":
                    if (message == "started")
                        CallStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Cyan);
                    else
                        CallStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
                    break;

                case "ended":
                case "error":
                    CallStatusText.Text = state.ToUpperInvariant();
                    CallTargetText.Text = message;
                    CallStatusDot.Fill = new SolidColorBrush(state == "error" ? Microsoft.UI.Colors.Red : Microsoft.UI.Colors.Gray);
                    CallHangupButton.Visibility = Visibility.Collapsed;
                    _callTimer?.Stop();
                    // Auto-hide after 5 seconds
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5000);
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (CallStatusOverlay is not null)
                                CallStatusOverlay.Visibility = Visibility.Collapsed;
                        });
                    });
                    break;
            }
        });
    }

    private void UpdateCallDuration()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _callDuration = _callDuration.Add(TimeSpan.FromSeconds(1));
            var mins = _callDuration.Minutes;
            var secs = _callDuration.Seconds;
            if (CallDurationText is not null)
                CallDurationText.Text = $"{mins:D2}:{secs:D2}";
        });
    }

    private void CallHangupButton_Click(object sender, RoutedEventArgs e)
    {
        _ = _gemini.SendTelegramCallStopAsync();
    }

    private void CallAcceptButton_Click(object sender, RoutedEventArgs e)
    {
        // For incoming calls (not implemented yet - we only do outgoing)
    }

    private void CallDeclineButton_Click(object sender, RoutedEventArgs e)
    {
        _ = _gemini.SendTelegramCallStopAsync();
    }
}
