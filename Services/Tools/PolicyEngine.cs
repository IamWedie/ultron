using System.Text;
using System.Text.Json;

namespace Ultron.Services;

/// <summary>Risk level plus the sentence shown in the approval prompt.</summary>
public sealed record PolicyDefinition(RiskLevel Risk, string Summary);

/// <summary>
/// The one place that decides whether a tool call runs, needs the user's OK, or
/// is refused outright.
/// </summary>
/// <remarks>
/// This replaces the old <c>RequiresApproval</c> boolean per tool, which could
/// only say yes/no and could not distinguish a read from a delete. Risk is
/// resolved per call, so <c>file_processor</c> can be a read (no prompt) or a
/// delete (prompt) depending on its action.
/// <para>
/// Unknown tools are denied. Adding a tool to a handler without adding it here
/// therefore fails closed instead of silently running unapproved.
/// </para>
/// </remarks>
public sealed class PolicyEngine : IActionPolicy
{
    private static readonly PolicyDefinition Allow0 = new(RiskLevel.Read, "Read-only query.");
    private static readonly PolicyDefinition Allow1 = new(RiskLevel.Low, "Low-risk action.");

    private readonly Dictionary<string, Func<ToolCall, PolicyDefinition>> _table;

    public PolicyEngine()
    {
        _table = new(StringComparer.OrdinalIgnoreCase)
        {
            // ── memory ───────────────────────────────────────────────────────
            ["save_memory"] = _ => new(RiskLevel.Low, "Save a fact to long-term memory: \"{value}\""),
            ["recall_memory"] = _ => new(RiskLevel.Read, "Search long-term memory for: \"{query}\""),

            // ── desktop input ─────────────────────────────────────────────────
            ["type_text"] = _ => new(RiskLevel.Medium, "Type text into the focused window: \"{text}\""),
            ["press_key"] = _ => new(RiskLevel.Medium, "Press key/shortcut: \"{keys}\""),
            ["desktop_control"] = c => new(RiskLevel.Medium, $"Desktop action: {ActionOf(c)}"),
            ["window_manage"] = c => new(RiskLevel.Medium, $"Window action: {ActionOf(c)}"),
            ["computer_use"] = c => new(RiskLevel.Critical, $"Run an autonomous desktop task: \"{Text(c, "task")}\""),

            // ── system ────────────────────────────────────────────────────────
            ["system_status"] = _ => Allow0,
            ["open_app"] = c => new(RiskLevel.Medium, $"Launch application: \"{Text(c, "app_name")}\""),
            ["computer_settings"] = c => new(RiskLevel.Medium, $"Change system setting: {ActionOf(c)}"),
            ["set_mic_device"] = c => new(RiskLevel.Medium, $"Switch microphone input to: \"{Text(c, "device")}\""),
            ["audio_devices_list"] = _ => Allow0,
            ["set_away_mode"] = c => new(RiskLevel.Low, $"Set away mode: {ActionOf(c)}"),
            ["shutdown_jarvis"] = _ => new(RiskLevel.Critical, "Shut down ULTRON completely."),

            // ── web / media ───────────────────────────────────────────────────
            ["web_search"] = c => new(RiskLevel.Low, $"Search the web for: \"{Text(c, "query")}\""),
            ["weather_report"] = c => new(RiskLevel.Read, $"Check the weather for: \"{Text(c, "city")}\""),
            ["browser_control"] = c => new(RiskLevel.Medium, $"Browser action: {ActionOf(c)} \"{Text(c, "url")}\""),
            ["youtube_video"] = c => new(RiskLevel.Low, $"YouTube action: {ActionOf(c)} \"{Text(c, "query")}\""),

            // ── files ─────────────────────────────────────────────────────────
            // Read-only actions run unprompted; anything that mutates asks first.
            ["file_processor"] = FilePolicy,

            // ── communication ─────────────────────────────────────────────────
            ["send_message"] = c => new(RiskLevel.High,
                $"Send a message to {Text(c, "receiver")} via {Text(c, "platform")}: \"{Text(c, "message_text")}\""),

            // ── misc ──────────────────────────────────────────────────────────
            ["reminder"] = _ => new(RiskLevel.Low, "Note a reminder."),
            ["code_helper"] = _ => new(RiskLevel.Low, "Analyze a code snippet."),
            ["create_document"] = c => new(RiskLevel.High, $"Create a document: {ActionOf(c)}"),
            ["task_inbox"] = _ => Allow1,
            ["flight_finder"] = c => new(RiskLevel.Low, $"Look up flights: \"{Text(c, "query")}\""),
            ["manage_monitor"] = c => new(RiskLevel.Medium, $"Change monitor layout: {ActionOf(c)}"),
            ["background_monitor"] = c => new(RiskLevel.Medium, $"Change background monitoring: {ActionOf(c)}"),
            ["guardian"] = c => new(RiskLevel.Medium, $"Run the guardian check: {ActionOf(c)}"),
            ["undo"] = _ => new(RiskLevel.High, "Undo the most recent assistant action."),

            // ── handled by the backend, never executed here ───────────────────
            ["screen_process"] = _ => new(RiskLevel.Read, "Analyse the screen."),
            ["close_camera"] = _ => new(RiskLevel.Low, "Close the camera."),
            ["set_voice"] = _ => Allow1,
            ["set_live_vision"] = _ => Allow0,
        };
    }

    private static PolicyDefinition FilePolicy(ToolCall call)
    {
        var action = (call.GetString("action") ?? "").Trim().ToLowerInvariant();
        return action switch
        {
            "read" or "list" or "browse" or "search" =>
                new(RiskLevel.Read, $"Read-only file action '{action}'."),
            "delete" =>
                new(RiskLevel.High, $"DELETE files: \"{Text(call, "file_path")}\""),
            "write" or "create" =>
                new(RiskLevel.Medium, $"Write a file: \"{Text(call, "file_path")}\""),
            "rename" or "move" or "copy" =>
                new(RiskLevel.Medium, $"{action} file: \"{Text(call, "file_path")}\""),
            _ =>
                // An unrecognised file action must not be waved through.
                new(RiskLevel.High, $"File action '{Text(call, "action")}'."),
        };
    }

    public PolicyDecision Evaluate(ToolCall toolCall)
    {
        if (!_table.TryGetValue(toolCall.Name, out var resolve))
        {
            return PolicyDecision.Deny(RiskLevel.Critical, $"No policy for tool '{toolCall.Name}'.");
        }

        var definition = resolve(toolCall);
        var outcome = definition.Risk <= RiskLevel.Low
            ? PolicyOutcome.Allow
            : PolicyOutcome.RequireApproval;

        return new PolicyDecision(outcome, definition.Risk, Render(definition.Summary, toolCall));
    }

    /// <summary>Substitutes {arg} placeholders in a summary template.</summary>
    private static string Render(string template, ToolCall call)
    {
        if (!template.Contains('{')) return template;

        var sb = new StringBuilder(template.Length + 32);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
            {
                sb.Append(template[i]);
                continue;
            }

            var close = template.IndexOf('}', i);
            if (close < 0)
            {
                sb.Append(template[i]);
                continue;
            }

            sb.Append(Text(call, template[(i + 1)..close]));
            i = close;
        }
        return sb.ToString();
    }

    private static string ActionOf(ToolCall call) => Text(call, "action");

    /// <summary>
    /// Renders one argument for display. Values are truncated: a model can send a
    /// multi-megabyte string, and an approval dialog must not try to show it.
    /// </summary>
    private static string Text(ToolCall call, string argument)
    {
        if (!call.Arguments.TryGetProperty(argument, out var el) ||
            el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "(none)";
        }

        var value = el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.GetRawText();
        const int limit = 160;
        return value.Length <= limit ? value : value[..limit] + "…";
    }
}
