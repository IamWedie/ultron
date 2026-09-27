// MainWindow.ComputerAct.cs - computer-use actions and approval
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
    private void OnComputerActRequested(string id, JsonElement action)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (!await RequestComputerActionApproval(action))
                {
                    await _gemini.SendComputerActResultAsync(id, "User denied the desktop action.");
                    return;
                }
                var result = ExecuteComputerAct(action);
                await _gemini.SendComputerActResultAsync(id, result);
            }
            catch (Exception ex)
            {
                await _gemini.SendComputerActResultAsync(id, $"Action failed: {ex.Message}");
            }
        });
    }

    private async Task<bool> RequestComputerActionApproval(JsonElement action)
    {
        await _approvalGate.WaitAsync();
        try
        {
            var description = action.GetRawText();
            if (description.Length > 600) description = description[..600] + "…";
            var dialog = new ContentDialog
            {
                XamlRoot = Content?.XamlRoot,
                Title = "DESKTOP ACTION APPROVAL REQUIRED",
                Content = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap },
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

    private string ExecuteComputerAct(JsonElement action)
    {
        var type = (action.TryGetProperty("type", out var t) ? t.GetString() : "")?.ToLowerInvariant() ?? "";
        var x = action.TryGetProperty("x", out var xe) && xe.ValueKind == JsonValueKind.Number ? (int?)xe.GetInt32() : null;
        var y = action.TryGetProperty("y", out var ye) && ye.ValueKind == JsonValueKind.Number ? (int?)ye.GetInt32() : null;
        var amount = action.TryGetProperty("amount", out var ae) && ae.ValueKind == JsonValueKind.Number ? ae.GetInt32() : 1;
        if ((x < 0 || y < 0 || x > 10000 || y > 10000) && type is "click" or "double_click" or "right_click" or "move")
            return "Desktop coordinates are outside the allowed range.";
        if (type == "scroll" && Math.Abs(amount) > 100)
            return "Scroll amount is outside the allowed range.";

        switch (type)
        {
            case "click":
                ClickAt(x, y);
                return $"Clicked at ({x ?? CurrentX()}, {y ?? CurrentY()}).";
            case "double_click":
                ClickAt(x, y, doubleClick: true);
                return $"Double-clicked at ({x ?? CurrentX()}, {y ?? CurrentY()}).";
            case "right_click":
                RightClickAt(x, y);
                return $"Right-clicked at ({x ?? CurrentX()}, {y ?? CurrentY()}).";
            case "move":
                MoveMouse(x, y);
                return $"Moved to ({x ?? CurrentX()}, {y ?? CurrentY()}).";
            case "scroll":
                Scroll(amount);
                return $"Scrolled {amount} notch(es).";
            case "type":
            case "type_text":
                var text = action.TryGetProperty("text", out var te) ? te.GetString() ?? "" : "";
                SendTypedText(text);
                return $"Typed: {text}";
            case "press":
            case "press_key":
                var keys = action.TryGetProperty("keys", out var ke) ? ke.GetString() ?? "" : "";
                var args = new Dictionary<string, object> { ["keys"] = keys };
                return HandlePressKey(args);
            default:
                return $"Unknown computer action type '{type}'.";
        }
    }

    private void OnGeminiError(string message)
    {
        DispatcherQueue.TryEnqueue(() => AddMessage("system", $"Gemini: {message}"));
    }

}
