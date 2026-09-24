using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;
using Ultron.Models;
using Windows.UI;

namespace Ultron.Services;

/// <summary>
/// Handles the Telegram call overlay UI (incoming/outgoing call status, duration, hangup).
/// Extracted from MainWindow to reduce its size and separate concerns.
/// </summary>
public sealed class TelegramCallOverlayService
{
    private readonly MainWindow _mainWindow;
    private readonly GeminiBackend _gemini;

    public TelegramCallOverlayService(MainWindow mainWindow, GeminiBackend gemini)
    {
        _mainWindow = mainWindow;
        _gemini = gemini;
    }

    public void HandleCallStateChanged(string state, string message)
    {
        _mainWindow.DispatcherQueue?.TryEnqueue(() =>
        {
            if (_mainWindow.CallStatusOverlay is null) return;

            switch (state.ToLowerInvariant())
            {
                case "ringing":
                    _mainWindow.CallStatusOverlay.Visibility = Visibility.Visible;
                    _mainWindow.CallStatusText.Text = "RINGING";
                    _mainWindow.CallTargetText.Text = message;
                    _mainWindow.CallDurationText.Text = "00:00";
                    _mainWindow._callDuration = TimeSpan.Zero;
                    _mainWindow.CallStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Orange);
                    _mainWindow.CallHangupButton.Visibility = Visibility.Visible;
                    _mainWindow.CallAcceptButton.Visibility = Visibility.Collapsed;
                    _mainWindow.CallDeclineButton.Visibility = Visibility.Collapsed;
                    _mainWindow._callTimer?.Start();
                    break;

                case "connecting":
                    _mainWindow.CallStatusText.Text = "CONNECTING";
                    _mainWindow.CallTargetText.Text = message;
                    _mainWindow.CallStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Orange);
                    break;

                case "connected":
                    _mainWindow.CallStatusText.Text = "CONNECTED";
                    _mainWindow.CallTargetText.Text = message;
                    _mainWindow.CallStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
                    _mainWindow.CallAcceptButton.Visibility = Visibility.Collapsed;
                    _mainWindow.CallDeclineButton.Visibility = Visibility.Collapsed;
                    break;

                case "remote_speech":
                    if (message == "started")
                        _mainWindow.CallStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Cyan);
                    else
                        _mainWindow.CallStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
                    break;

                case "ended":
                case "error":
                    _mainWindow.CallStatusText.Text = state.ToUpperInvariant();
                    _mainWindow.CallTargetText.Text = message;
                    _mainWindow.CallStatusDot.Fill = new SolidColorBrush(state == "error" ? Microsoft.UI.Colors.Red : Microsoft.UI.Colors.Gray);
                    _mainWindow.CallHangupButton.Visibility = Visibility.Collapsed;
                    _mainWindow._callTimer?.Stop();
                    // Auto-hide after 5 seconds
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5000);
                        _mainWindow.DispatcherQueue?.TryEnqueue(() =>
                        {
                            if (_mainWindow.CallStatusOverlay is not null)
                                _mainWindow.CallStatusOverlay.Visibility = Visibility.Collapsed;
                        });
                    });
                    break;
            }
        });
    }

    public void UpdateCallDuration()
    {
        _mainWindow.DispatcherQueue?.TryEnqueue(() =>
        {
            _mainWindow._callDuration = _mainWindow._callDuration.Add(TimeSpan.FromSeconds(1));
            var mins = _mainWindow._callDuration.Minutes;
            var secs = _mainWindow._callDuration.Seconds;
            if (_mainWindow.CallDurationText is not null)
                _mainWindow.CallDurationText.Text = $"{mins:D2}:{secs:D2}";
        });
    }

    public void CallHangupButton_Click(object sender, RoutedEventArgs e)
    {
        _ = _mainWindow._gemini.SendTelegramCallStopAsync();
    }

    public void CallAcceptButton_Click(object sender, RoutedEventArgs e)
    {
        // For incoming calls (not implemented yet - we only do outgoing)
    }

    public void CallDeclineButton_Click(object sender, RoutedEventArgs e)
    {
        _ = _mainWindow._gemini.SendTelegramCallStopAsync();
    }
}