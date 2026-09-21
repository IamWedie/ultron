using System.Text.Json;
using System.Text.Json.Nodes;
using Ultron.Models;

namespace Ultron.Services;

public sealed class ApprovalRequest
{
    public required string ToolName { get; init; }
    public required string Description { get; init; }
    public Action<bool>? Resolve { get; init; }
}

public sealed class Brain : IDisposable
{
    private readonly MemoryStore _memory;
    private readonly List<ChatMessage> _history = new();
    private readonly SemaphoreSlim _roundGate = new(1, 1);

    public event Action<ApprovalRequest>? ApprovalRequested;

    public Brain(AppSettings settings, MemoryStore memory)
    {
        _memory = memory;
    }

    public void ResetHistory() => _history.Clear();

    public IReadOnlyList<ChatMessage> History => _history;

    private async Task<string> MemoryContextAsync(string userText, string? speaker)
    {
        try
        {
            var parts = new List<string>();
            var facts = await _memory.ListFactsAsync();
            var q = userText.ToLowerInvariant();
            var matches = facts.Where(f => f.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
            if (matches.Count > 0)
                parts.Add("Known facts:\n" + string.Join("\n", matches.Select(f => $"- {f}")));
            var recent = await _memory.RecentConversationsAsync(10);
            if (recent.Count > 0)
                parts.Add("Recent conversation:\n" + string.Join("\n", recent.Select(r => $"{r.Role}: {r.Text[..Math.Min(160, r.Text.Length)]}")));
            if (parts.Count == 0) return "";
            return "\n\nLOCAL MEMORY (stored on the user's PC):\n" + string.Join("\n\n", parts);
        }
        catch
        {
            return "";
        }
    }

    public async Task<string> AskAsync(string userText, Action<string>? onChunk = null, string? speaker = null, string source = "ui")
    {
        // Thin fallback: just return memory context, no LLM call
        var context = await MemoryContextAsync(userText, speaker);
        if (!string.IsNullOrEmpty(context))
        {
            return $"[Local memory context]\n{context}\n\nI'm running in local mode without a configured brain. Set a Gemini API key for full AI responses.";
        }
        return "I'm running in local mode without a configured brain. Set a Gemini API key for full AI responses.";
    }

    public string SendSms(string number, string message)
    {
        try
        {
            var json = new JsonObject
            {
                ["number"] = number,
                ["message"] = message,
            };
            return PhoneTools.Execute(null, "phone_send_sms", json);
        }
        catch (Exception e)
        {
            return "SMS failed: " + e.Message;
        }
    }

    private async Task<string> ExecuteToolAsync(string toolName, JsonNode? json)
    {
        try
        {
            switch (toolName)
            {
                case "remember_fact":
                {
                    var fact = json?["fact"]?.GetValue<string>() ?? "";
                    await _memory.AddFactAsync(fact);
                    return "Fact remembered.";
                }
                case "recall_memories":
                {
                    var query = json?["query"]?.GetValue<string>() ?? "";
                    return await RecallMemoryAsync(query);
                }
                default:
                    return "Tool not available in local mode.";
            }
        }
        catch (Exception e)
        {
            return $"Tool execution failed: {e.Message}";
        }
    }

    private async Task<string> RecallMemoryAsync(string query)
    {
        var ql = query.ToLowerInvariant();
        var facts = (await _memory.ListFactsAsync()).Where(f => f.Contains(ql, StringComparison.OrdinalIgnoreCase)).ToList();
        var convs = await _memory.SearchConversationsAsync(query, 5);
        var parts = new List<string>();
        if (facts.Count > 0) parts.Add("Facts:\n" + string.Join("\n", facts.Select(f => $"- {f}")));
        if (convs.Count > 0) parts.Add("Past conversation:\n" + string.Join("\n", convs.Select(c => $"[{c.Ts}] {c.Role}: {c.Text[..Math.Min(200, c.Text.Length)]}")));
        if (parts.Count == 0) return "Nothing found in memory about that.";
        return string.Join("\n", parts);
    }

    public void Dispose() { }
}