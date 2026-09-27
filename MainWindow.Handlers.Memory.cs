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
        var category = args.GetValueOrDefault("category")?.ToString() ?? "notes";
        var key = args.GetValueOrDefault("key")?.ToString() ?? "";
        var value = args.GetValueOrDefault("value")?.ToString() ?? "";
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
            return ToolResult.InvalidArguments("save_memory needs both a 'key' and a 'value'.");
        
        // Store in SQLite MemoryStore as a fact with topic = category
        var factText = $"[{category}] {key}: {value}";
        _ = Task.Run(async () =>
        {
            try { await _memory.AddFactAsync(factText, category); }
            catch (Exception ex) { AppLog.Write("MainWindow", $"AddFactAsync failed: {ex.Message}", AppLog.Level.Error); }
        });
        
        PushUndo($"save_memory([{category}] {key})", () =>
        {
            // Note: MemoryStore doesn't support fact removal by key, so we can't fully undo
            return "Fact stored in memory (manual removal needed if desired).";
        });
        
        if (_memCache.Count > 0) RefreshMemory();
        return ToolResult.Ok($"Remembered [{category}] {key}: {value}");
    }

    private ToolResult HandleRecallMemory(Dictionary<string, object> args)
    {
        var query = args.GetValueOrDefault("query")?.ToString() ?? "";
        
        // Search both facts and conversations in SQLite MemoryStore
        var results = new List<string>();
        
        // Search facts
        var factsTask = Task.Run(async () =>
        {
            try { return await _memory.ListFactsAsync(); }
            catch { return new List<string>(); }
        });
        
        // Search conversations
        var convsTask = Task.Run(async () =>
        {
            try { return await _memory.SearchConversationsAsync(query, 6); }
            catch { return new List<ConversationRow>(); }
        });
        
        Task.WaitAll(factsTask, convsTask);
        
        var facts = factsTask.Result;
        var convs = convsTask.Result;
        
        var q = query.ToLowerInvariant();
        foreach (var f in facts)
        {
            if (q.Length == 0 || f.ToLowerInvariant().Contains(q))
                results.Add($"[fact] {f}");
        }
        foreach (var c in convs)
        {
            if (q.Length == 0 || c.Text.ToLowerInvariant().Contains(q))
                results.Add($"[conv] {c.Ts} {c.Role}: {c.Text}");
        }
        
        // An empty recall is a miss, not a success: reporting it as Ok let the model
        // tell the user what it remembered when it had recalled nothing.
        return results.Count > 0
            ? ToolResult.Ok(string.Join("\n", results.Take(8)))
            : ToolResult.NotFound($"Nothing found in memory for '{query}'.");
    }

}
