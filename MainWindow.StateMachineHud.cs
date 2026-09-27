// MainWindow.StateMachineHud.cs - state machine HUD
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
    /* ===================== STATE MACHINE HUD ===================== */

    private void OnStateChanged(StateTransition t)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyStateVisual(t.To);
            LogEnqueued("system", $"State {t.From} → {t.To} ({t.Reason}).");
        });
    }

    private void ApplyStateVisual(AssistantState st)
    {
        var (fill, stroke, label, dot, ticker) = st switch
        {
            AssistantState.Sleep => ("#1AFF2E2E", "#66FF2E2E", "STANDBY", "#FF9BA1AB", "CORE STATUS: STANDBY"),
            AssistantState.WakeListening => ("#33FFB703", "#FFFFB703", "WAKE LISTEN", "#FFFFB703", "CORE STATUS: WAKE LISTEN"),
            AssistantState.Engaged => ("#66FF2E2E", "#FFFF2E2E", "ENGAGED", "#FFFF2E2E", "CORE STATUS: ENGAGED"),
            AssistantState.Rest => ("#22FF2E2E", "#AAFF2E2E", "REST", "#AAFF2E2E", "CORE STATUS: REST"),
            _ => ("#1AFF2E2E", "#66FF2E2E", "STANDBY", "#FF9BA1AB", "CORE STATUS: STANDBY"),
        };
        OrbContainer.Fill = BrushFromHex(fill);
        OrbContainer.Stroke = BrushFromHex(stroke);
        OrbStateLabel.Text = label;
        TickerDot.Fill = BrushFromHex(dot);
        TickerText.Text = ticker;
    }

    private void SetMicVisual(bool busy)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            MicButton.Foreground = busy
                ? BrushFromHex("#FFFF2E2E")
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 201, 206, 214));
        });
    }

    private void LogEnqueued(string who, string text)
    {
        DispatcherQueue.TryEnqueue(() => AddMessage(who, text));
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        var s = hex.TrimStart('#');
        return new SolidColorBrush(Color.FromArgb(
            byte.Parse(s[..2], NumberStyles.HexNumber),
            byte.Parse(s[2..4], NumberStyles.HexNumber),
            byte.Parse(s[4..6], NumberStyles.HexNumber),
            byte.Parse(s[6..8], NumberStyles.HexNumber)));
    }

}
