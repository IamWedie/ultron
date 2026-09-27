// MainWindow.Handlers.Web.cs - web_search, weather, reminder, browser_control
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
    private async Task<ToolResult> HandleWebSearchAsync(Dictionary<string, object> args)
    {
        var query = args.GetValueOrDefault("query")?.ToString() ?? "";
        if (string.IsNullOrEmpty(query)) return ToolResult.InvalidArguments("web_search needs a non-empty 'query' argument.");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
            var html = await http.GetStringAsync(url);
            // Extract text snippets from DDG HTML
            var results = new List<string>();
            var idx = 0;
            while (idx < html.Length && results.Count < 5)
            {
                var start = html.IndexOf("result__snippet", idx, StringComparison.Ordinal);
                if (start < 0) break;
                var linkStart = html.IndexOf('>', start);
                var linkEnd = html.IndexOf("</a>", linkStart, StringComparison.Ordinal);
                if (linkEnd < 0) break;
                var snippet = System.Net.WebUtility.HtmlDecode(html[(linkStart + 1)..linkEnd]).Trim();
                if (!string.IsNullOrEmpty(snippet))
                    results.Add(snippet);
                idx = linkEnd + 4;
            }
            return results.Count > 0
                ? ToolResult.Ok($"Search results for \"{query}\":\n" + string.Join("\n", results.Select((r, i) => $"{i + 1}. {r}")))
                : ToolResult.NotFound($"No results found for \"{query}\".");
        }
        catch (Exception ex) { return ToolResult.Fail($"Search failed: {ex.Message}"); }
    }

    private async Task<ToolResult> HandleWeatherAsync(Dictionary<string, object> args)
    {
        var city = args.GetValueOrDefault("city")?.ToString() ?? "";
        if (string.IsNullOrEmpty(city)) return ToolResult.InvalidArguments("weather_report needs a non-empty 'city' argument.");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var url = $"https://wttr.in/{Uri.EscapeDataString(city)}?format=3";
            var result = await http.GetStringAsync(url);
            return ToolResult.Ok(result.Trim());
        }
        catch (Exception ex) { return ToolResult.Fail($"Weather lookup failed: {ex.Message}"); }
    }

    private ToolResult HandleReminder(Dictionary<string, object> args)
    {
        var date = args.GetValueOrDefault("date")?.ToString() ?? "";
        var time = args.GetValueOrDefault("time")?.ToString() ?? "";
        var message = args.GetValueOrDefault("message")?.ToString() ?? "";
        // Reminder persistence is not yet implemented; log it for now.
        var logEntry = $"[Reminder] {date} {time} — {message}";
        _ = Task.Run(async () =>
        {
            try { await _memory.LogAsync("system", logEntry); }
            catch { }
        });
        // Nothing will fire at the requested time. Reporting that as a success
        // meant the model promised the user a reminder that never arrived.
        return ToolResult.Unsupported(
            $"Reminder NOT scheduled. It was only logged: {date} {time} — {message}. Scheduled reminders are not implemented; tell the user nothing will fire.");
    }

    private async Task<ToolResult> HandleBrowserControlAsync(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
        var url = args.GetValueOrDefault("url")?.ToString() ?? "";
        var query = args.GetValueOrDefault("query")?.ToString() ?? "";
        if (action == "open_url" && !string.IsNullOrEmpty(url))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
                return ToolResult.InvalidArguments("Only absolute http or https URLs are allowed.");
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
                return ToolResult.Ok($"Opened {uri.AbsoluteUri}");
            }
            catch (Exception ex) { return ToolResult.Fail($"Failed to open {uri.AbsoluteUri}: {ex.Message}"); }
        }
        if (action == "search" && !string.IsNullOrEmpty(query))
        {
            var searchUrl = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}";
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(searchUrl) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
                return ToolResult.Ok($"Searched for \"{query}\"");
            }
            catch (Exception ex) { return ToolResult.Fail($"Search failed: {ex.Message}"); }
        }
        return ToolResult.Unsupported(
            $"Browser control action '{action}' not recognised or missing parameters. Supported: open_url (with 'url'), search (with 'query').");
    }

}
