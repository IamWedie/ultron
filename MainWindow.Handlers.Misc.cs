// MainWindow.Handlers.Misc.cs - code_helper, send_message, set_away, contacts, youtube
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
    private ToolResult HandleCodeHelper(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
        var code = args.GetValueOrDefault("code")?.ToString() ?? "";
        var instruction = args.GetValueOrDefault("instruction")?.ToString() ?? "";
        if (string.IsNullOrEmpty(code)) return ToolResult.InvalidArguments("code_helper needs a non-empty 'code' argument.");
        // This tool is a placeholder - code analysis is done by the AI model directly.
        // It reported itself as a success, so the model told the user the analysis
        // was done when no code had been touched at all.
        return ToolResult.Unsupported(
            $"Code helper ({action}) is not implemented. The AI can analyze code directly in conversation - send the code in your message instead of calling this tool.");
    }

    private ToolResult HandleSendMessage(Dictionary<string, object> args)
    {
        var receiver = args.GetValueOrDefault("receiver")?.ToString() ?? "";
        var text = args.GetValueOrDefault("message_text")?.ToString() ?? "";
        var platform = args.GetValueOrDefault("platform")?.ToString() ?? "sms";
        // Actual sending requires ADB/phone integration; this tool logs the intent.
        var logEntry = $"[Message to {receiver} via {platform}] {text}";
        _ = Task.Run(async () =>
        {
            try { await _memory.LogAsync("system", logEntry); }
            catch { }
        });
        // The message is logged, not sent. Saying so as a success let the model
        // confirm delivery of a text that never left the machine.
        return ToolResult.Unsupported(
            $"Message NOT sent. It was only logged: \"{text}\" for {receiver} via {platform}. Sending requires the phone_* ADB tools. Tell the user the message was not delivered.");
    }

    private string HandleSetAway(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString()?.ToLowerInvariant() ?? "on";
        var on = action is not ("off" or "false" or "0" or "disable");
        _settings.AwayMode = on;
        _settings.Save();
        _ = _gemini.SetAwayAsync(on);
        Dbg($"Away mode set to {on}.");
        return on
            ? "Away mode is ON. I will watch your system and message you with anything important."
            : "Away mode is OFF. I am back to speaking alerts here.";
    }

    private void OnContactRequested(ContactRequest req)
    {
        Dbg($"Contact requested [{req.Area}/{req.Priority}]: {AppLog.Redact(req.Text)}");
        _ = RouteContactAsync(req);
    }

    private async Task RouteContactAsync(ContactRequest req)
    {
        try
        {
            var result = await _outreach.SendAsync(req.Text, req.Area, req.Priority);
            LogEnqueued("system", $"Contact [{req.Area}]: {result}");
        }
        catch (Exception ex)
        {
            Dbg($"Contact routing failed: {ex.Message}");
        }
    }

    private ToolResult HandleYouTubeVideo(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
        var query = args.GetValueOrDefault("query")?.ToString() ?? "";
        if (action == "search" && !string.IsNullOrEmpty(query))
        {
            var url = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}";
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
                return ToolResult.Ok($"Searching YouTube for \"{query}\"");
            }
            catch (Exception ex) { return ToolResult.Fail($"Failed to open YouTube: {ex.Message}"); }
        }
        return ToolResult.Unsupported(
            $"YouTube action '{action}' not recognised. Supported: search (with a 'query').");
    }

}
