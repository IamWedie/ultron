// MainWindow.Handlers.Memory.cs - save_memory, recall_memory
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
    private ToolResult HandleSaveMemory(Dictionary<string, object> args)
    {
        if (!_settings.MemoryLogging)
            return ToolResult.Ok("Memory logging is off, so nothing was saved. The user can turn it back on in Settings.");

        var category = args.GetValueOrDefault("category")?.ToString() ?? "notes";
        var key = args.GetValueOrDefault("key")?.ToString() ?? "";
        var value = args.GetValueOrDefault("value")?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            return ToolResult.InvalidArguments("save_memory needs both a 'key' and a 'value'.");

        try
        {
            var stored = _facts.Upsert(category, key, value);
            RefreshMemory();
            return ToolResult.Ok($"Remembered [{stored.Category}] {stored.Key}: {stored.Value}");
        }
        catch (InvalidOperationException ex)
        {
            // The only expected throw: an unparseable long_term.json, which the
            // store refuses to overwrite. Report it rather than losing the data.
            AppLog.Write("MainWindow", "save_memory refused: " + ex.Message, AppLog.Level.Warn);
            return ToolResult.Fail($"Could not save to memory: {ex.Message}");
        }
    }

    private ToolResult HandleRecallMemory(Dictionary<string, object> args)
    {
        if (!_settings.MemoryLogging)
            return ToolResult.Ok("Memory logging is off, so there is nothing stored to recall.");

        var query = args.GetValueOrDefault("query")?.ToString() ?? "";

        // Same ranked search the Memory panel and the local fallback use, so all
        // three read paths agree on what "recall" means.
        var hits = _facts.Recall(query, 8);
        if (hits.Count == 0)
        {
            // An empty recall is a miss, not a success: reporting it as Ok let the
            // model tell the user what it remembered when it had recalled nothing.
            return ToolResult.NotFound($"Nothing found in memory for '{query}'.");
        }

        return ToolResult.Ok(string.Join("\n",
            hits.Select(f => $"[{f.Category}] {f.Key}: {f.Value}")));
    }
}
