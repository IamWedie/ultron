// MainWindow.ToolRouting.cs - tool routing, legacy arg flattening, undo ledger
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
    /// <summary>
    /// Flattens typed arguments for handlers that have not been migrated yet.
    /// Mirrors the old JsonElement.ToString() behaviour so no handler changes
    /// meaning while it is being moved into a tool service.
    /// </summary>
    private static Dictionary<string, object> ToLegacyArgs(JsonElement arguments)
    {
        var legacy = new Dictionary<string, object>();
        foreach (var prop in arguments.EnumerateObject())
            legacy[prop.Name] = prop.Value.ToString();
        return legacy;
    }

    private string Shutdown()
    {
        ScheduleShutdown(4);
        return "Understood. Shutting down now.";
    }

    /// <summary>
    /// Finds a freshly launched app's window, focuses it and makes it the typing
    /// target, so "open X, then type Y" behaves as a user expects.
    ///
    /// Win32 window handles stay in the UI layer, but the wait is awaited rather
    /// than slept: tool calls are dispatched onto the UI thread, so blocking here
    /// froze the whole window for up to four seconds while the app started.
    /// </summary>
    async Task<IntPtr> ILaunchedWindowActivator.ActivateAsync(
        string imageName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var hwnd = Native.FindMainWindow(imageName);
            if (hwnd != IntPtr.Zero)
            {
                _lastTargetWindow = hwnd;
                Native.FocusWindow(hwnd);
                return hwnd;
            }
            await Task.Delay(200, cancellationToken).ConfigureAwait(true);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Shows the approval dialog. This is the only place the tool layer touches
    /// the UI, and it is reached through <see cref="IApprovalService"/> - no tool
    /// service ever holds a reference to this window.
    /// </summary>
    async Task<bool> IApprovalService.RequestApprovalAsync(
        ToolCall toolCall, PolicyDecision decision, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;

        // One dialog at a time: overlapping prompts would race the user's answer.
        await _approvalGate.WaitAsync(cancellationToken);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content?.XamlRoot,
                Title = decision.Risk >= RiskLevel.High
                    ? "HIGH-RISK ACTION NEEDS APPROVAL"
                    : "TOOL APPROVAL REQUIRED",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = $"ULTRON wants to execute: {toolCall.Name}", TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new TextBlock { Text = decision.Summary, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 155, 161, 171)) },
                        new TextBlock { Text = $"Risk: {decision.Risk}", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 120, 128, 140)) },
                    }
                },
                PrimaryButtonText = "Approve",
                CloseButtonText = "Deny",
            };

            if (dialog.XamlRoot == null) return false;
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _approvalGate.Release();
        }
    }

    private readonly SemaphoreSlim _approvalGate = new(1, 1);

    /* ===================== UNDO LEDGER ===================== */

    // The ledger itself lives in Services/UndoLedger so the file and app services
    // can register reversals without knowing about this window.
    private void PushUndo(string desc, Func<string> undo) => _undo.Push(desc, undo);

    private string UndoHistory() => _undo.History();

    /// <summary>
    /// UndoLedger answers with a sentence, and that sentence is either a reversal
    /// or a refusal - "Nothing to undo." when the ledger is empty, or "Undo of X
    /// failed: ..." when the reversal threw. Reporting either as a success told
    /// the user the change had been rolled back when it had not.
    /// </summary>
    private ToolResult HandleUndo() => _undo.UndoResult();

}
