// MainWindow.StateMachineHud.cs - state machine HUD
// Part of MainWindow. Fields and composition root live in MainWindow.xaml.cs.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
        var (eye, brow, label, dot, ticker, lid) = st switch
        {
            AssistantState.Sleep => ("#8C1E1E1E", "#4DFF2E2E", "STANDBY", "#FF9BA1AB", "CORE STATUS: STANDBY", 0.10),
            AssistantState.Engaged => ("#FFFF2E2E", "#FFFF2E2E", "ENGAGED", "#FFFF2E2E", "CORE STATUS: ENGAGED", 1.00),
            AssistantState.Rest => ("#CC6B1E1E", "#8CFF2E2E", "REST", "#AAFF2E2E", "CORE STATUS: REST", 0.45),
            _ => ("#8C1E1E1E", "#4DFF2E2E", "STANDBY", "#FF9BA1AB", "CORE STATUS: STANDBY", 0.10),
        };
        _faceLid = lid;
        SetEyeBrush(EyeLeftBrush, eye);
        SetEyeBrush(EyeRightBrush, eye);
        FaceBrowBrush.Color = ColorOfHex(brow);
        FaceContourBrush.Color = ColorOfHex(brow);
        EyeLeftScale.ScaleY = lid;
        EyeRightScale.ScaleY = lid;
        OrbStateLabel.Text = label;
        TickerDot.Fill = BrushFromHex(dot);
        TickerText.Text = ticker;
        ToolTipService.SetToolTip(UltronFace, $"Core status: {label}");
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

    /// <summary>Paints the mic toggle so its state is readable at a glance:
    /// red when the mic is streaming, grey when it is shut. The old build had no
    /// visual link between the button and the mic, which is part of why "the
    /// toggle does nothing" was so hard to see from the outside. Only ever called
    /// from the backend status handler, so the paint cannot claim a state the
    /// backend has not reported.</summary>
    private void SetMicToggleVisual(bool micOpen)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            MicToggleButton.Foreground = micOpen
                ? BrushFromHex("#FFFF2E2E")
                : BrushFromHex("#FF6B7078");
            ToolTipService.SetToolTip(MicToggleButton, micOpen
                ? "Microphone is open. Click to close it."
                : "Microphone is closed. Click to open it.");
        });
    }

    private void LogEnqueued(string who, string text)
    {
        DispatcherQueue.TryEnqueue(() => AddMessage(who, text));
    }

    private static void SetEyeBrush(LinearGradientBrush brush, string eyeHex)
    {
        var c = ColorOfHex(eyeHex);
        // Top stop stays brighter than the base so the lens highlight survives
        // even when the eye itself is dimmed for sleep.
        brush.GradientStops[0].Color = Color.FromArgb(
            (byte)Math.Min(255, c.A + 64), c.R, c.G, c.B);
        brush.GradientStops[1].Color = c;
    }

    private static SolidColorBrush BrushFromHex(string hex) => new(ColorOfHex(hex));

    private static Color ColorOfHex(string hex)
    {
        var s = hex.TrimStart('#');
        return Color.FromArgb(
            byte.Parse(s[..2], NumberStyles.HexNumber),
            byte.Parse(s[2..4], NumberStyles.HexNumber),
            byte.Parse(s[4..6], NumberStyles.HexNumber),
            byte.Parse(s[6..8], NumberStyles.HexNumber));
    }

}
