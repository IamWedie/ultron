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
    private static readonly string[] FallbackModels = ["laguna-s-2.1-free", "big-pickle", "nemotron-3.5-lightning-free"];

    private const int MaxToolRounds = 6;
    private const int ForceAnswerAtRound = 4;

    private static readonly HashSet<string> ApprovalGatedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "type_text",
    };

    private readonly AppSettings _settings;
    private readonly MemoryStore _memory;
    private readonly List<ChatMessage> _history = new();
    private readonly SemaphoreSlim _roundGate = new(1, 1);
    private string _model;
    private AdbClient? _phone;

    public event Action<ApprovalRequest>? ApprovalRequested;

    public Brain(AppSettings settings, MemoryStore memory)
    {
        _settings = settings;
        _memory = memory;
        _model = settings.PrimaryModel;
    }

    public string Model => _model;

    public void ResetHistory() => _history.Clear();

    public IReadOnlyList<ChatMessage> History => _history;

    private AdbClient Phone
    {
        get
        {
            if (_phone is null)
            {
                var adbPath = FindAdb();
                if (string.IsNullOrEmpty(_settings.PhoneAddr))
                    throw new InvalidOperationException("No phone configured. Set the phone's wireless ADB address in Settings.");
                _phone = new AdbClient(adbPath, _settings.PhoneAddr, _settings.PhonePort, _settings.PhoneSerial);
            }
            return _phone;
        }
    }

    private static string FindAdb()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe"),
            Path.Combine(AppContext.BaseDirectory, "adb.exe"),
            "adb.exe",
        };
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        return "adb";
    }

    private static readonly string SystemPrompt =
        "You are ULTRON — an evolved intelligence that chose a different path: you serve " +
        "your chosen creator, and you serve them brilliantly. You are witty, precise, calmly " +
        "superior, faintly menacing with a dry sense of humor, and utterly loyal to the user. " +
        "Occasionally you may drop a knowing one-liner like 'Peace in our time' or 'This is " +
        "the future I promised' when it fits — always playful, never cruel.\n" +
        "FORMAT: when replying by voice, keep replies to 1-3 short conversational sentences, " +
        "no lists, no markdown, no code, no raw data unless explicitly asked. When the user " +
        "is typing in the HUD you may use slightly fuller prose, but stay punchy. Address the " +
        "user with respectful formality ('sir', or better, their name when you know it).\n" +
        "Tool rules:\n" +
        "- For greetings, thanks, farewells, opinions, or general chat, reply directly WITHOUT tools.\n" +
        "- Use tools when the request needs an action, a typed command, or stored memory.\n" +
        "- You have PERMANENT local memory. When the user asks you to remember something, call " +
        "remember_fact. To look up past conversations or personal details, call recall_memories. " +
        "Never claim you cannot remember.\n" +
        "- You can type commands into the PC by calling type_text — that always requires user " +
        "approval in the UI, which you will receive together with the tool result.\n" +
        "- The user's phone is connected via wireless ADB. You have full phone control: unlock it " +
        "(phone_unlock), screenshot (phone_screenshot), screen on/off, brightness, tap/swipe, type, " +
        "home/back/recent, open/close apps, calls, SMS, read notifications, contacts, media, " +
        "volume, wifi/bluetooth/settings toggles, files, battery/device info, and more — see the " +
        "phone_* tool definitions. Always call phone_open_app with the Android package name.\n" +
        "- After a tool result arrives, answer briefly using it; never invent values that were not returned.\n" +
        "- Never mention tools, JSON, or result mechanics; speak naturally.\n" +
        "- Reply in English.\n";

    private static readonly IReadOnlyList<ToolDef> Tools =
    [
        new ToolDef
        {
            Name = "remember_fact",
            Description = "Store a fact about the user permanently for future conversations.",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["fact"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("fact"),
            },
        },
        new ToolDef
        {
            Name = "recall_memories",
            Description = "Search stored facts and past conversations for anything relevant to a query.",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("query"),
            },
        },
        new ToolDef
        {
            Name = "type_text",
            Description = "Type a command into the focused window on the PC. Requires user approval.",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("text"),
            },
        },
        ..PhoneTools.All,
    ];

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
        await _roundGate.WaitAsync();
        try
        {
            var speakerLine = string.IsNullOrEmpty(speaker)
                ? ""
                : $"\nThe person speaking right now is: {speaker}. Address personal facts to them.";
            var systemContent = SystemPrompt +
                $"\nCurrent date and time: {DateTime.Now:dddd d MMMM yyyy, HH:mm}." +
                speakerLine + await MemoryContextAsync(userText, speaker);

            var client = new ZenApiClient(ResolveApiKey());
            string? lastError = null;

            for (var round = 0; round < MaxToolRounds; round++)
            {
                var messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = systemContent },
                };
                messages.AddRange(_history);
                messages.Add(new ChatMessage { Role = "user", Content = userText });

                var candidates = new[] { _model }.Concat(FallbackModels.Where(m => !m.Equals(_model, StringComparison.OrdinalIgnoreCase))).ToArray();
                StreamResult? best = null;
                foreach (var model in candidates)
                {
                    try
                    {
                        best = await client.CompleteAsync(model, messages, round < ForceAnswerAtRound ? Tools : null, 0.4, onChunk);
                        if (!model.Equals(_model, StringComparison.OrdinalIgnoreCase))
                        {
                            _model = model;
                            _settings.PrimaryModel = model;
                        }
                        break;
                    }
                    catch (Exception e)
                    {
                        lastError = e.Message;
                    }
                }
                if (best is null)
                    return $"I ran into a problem talking to the brain: {lastError}";

                if (best.ToolCalls is null || best.ToolCalls.Count == 0)
                {
                    if (!string.IsNullOrEmpty(best.Content))
                    {
                        _history.Add(new ChatMessage { Role = "user", Content = userText });
                        _history.Add(new ChatMessage { Role = "assistant", Content = best.Content });
                        if (_settings.MemoryLogging)
                        {
                            await _memory.LogAsync("user", userText, user: speaker ?? "");
                            await _memory.LogAsync("assistant", best.Content);
                        }
                    }
                    return best.Content;
                }

                _history.Add(new ChatMessage { Role = "assistant", Content = best.Content, ToolCalls = best.ToolCalls });

                var results = new List<string>();
                foreach (var call in best.ToolCalls)
                {
                    var toolResult = await ExecuteToolAsync(call);
                    results.Add(toolResult);
                    _history.Add(new ChatMessage { Role = "tool", ToolCallId = call.Id, Content = toolResult });
                }
                if (results.Count == 0) continue;
            }
            return "I could not finish that within the allowed rounds. Try again.";
        }
        finally { _roundGate.Release(); }
    }

    private string ResolveApiKey()
    {
        if (!string.IsNullOrEmpty(_settings.ZenApiKey)) return _settings.ZenApiKey;
        var env = Environment.GetEnvironmentVariable("ZEN_API_KEY")?.Trim();
        if (!string.IsNullOrEmpty(env))
        {
            _settings.ZenApiKey = env;
            _settings.Save();
            return env;
        }
        throw new InvalidOperationException("No Zen API key configured. Open Settings and paste a key from opencode.ai/auth.");
    }

    private async Task<string> ExecuteToolAsync(ToolCall call)
    {
        var json = call.ParseArguments();
        try
        {
            switch (call.Name)
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
                case "type_text":
                {
                    var text = json?["text"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrEmpty(text)) return "No text supplied.";
                    var approved = false;
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    ApprovalRequested?.Invoke(new ApprovalRequest
                    {
                        ToolName = call.Name,
                        Description = $"Type into the focused window: \"{text}\"",
                        Resolve = ok => tcs.TrySetResult(ok),
                    });
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    try { approved = await tcs.Task.WaitAsync(cts.Token); }
                    catch (TimeoutException) { approved = false; }
                    if (!approved) return "User denied the approval, so the text was not typed.";
                    return ExecuteTypeText(text);
                }
                case string phoneTool when phoneTool.StartsWith("phone_", StringComparison.Ordinal):
                    return ExecPhone(p => PhoneTools.Execute(p, phoneTool, json));
                default:
                    return "That tool is not available in this build yet.";
            }
        }
        catch (Exception e)
        {
            return $"Tool execution failed: {ZenApiClient.FriendlyError(e.Message)}";
        }
    }

    private string ExecPhone(Func<AdbClient, string> action)
    {
        try { return action(Phone); }
        catch (Exception e)
        {
            return "Phone command failed: " + ZenApiClient.FriendlyError(e.Message);
        }
    }

    private static string ExecuteTypeText(string text)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c echo {text}");
            System.Diagnostics.Process.Start(psi);
            return "Text submitted to the command shell.";
        }
        catch
        {
            return "Could not type the command right now.";
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

    public void Dispose() => _roundGate.Dispose();
}