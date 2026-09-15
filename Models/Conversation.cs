using System.Text.Json.Nodes;

namespace Ultron.Models;

public sealed record ToolCall(string Id, string Name, string Arguments)
{
    public JsonObject? ParseArguments()
    {
        try { return JsonNode.Parse(Arguments)?.AsObject(); }
        catch { return null; }
    }
}

public sealed class ChatMessage
{
    public required string Role { get; init; }
    public string? Content { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }

    public JsonObject ToJson()
    {
        var obj = new JsonObject { ["role"] = Role };
        if (Content is not null) obj["content"] = Content;
        if (ToolCalls is not null && ToolCalls.Count > 0)
        {
            var calls = new JsonArray();
            foreach (var tc in ToolCalls)
            {
                calls.Add(new JsonObject
                {
                    ["id"] = tc.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = tc.Name, ["arguments"] = tc.Arguments },
                });
            }
            obj["tool_calls"] = calls;
        }
        if (ToolCallId is not null) obj["tool_call_id"] = ToolCallId;
        return obj;
    }
}

public sealed class ToolFunction
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public JsonObject? Parameters { get; init; }
}

public sealed class ToolDef
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public JsonObject? Parameters { get; init; }

    public JsonObject ToJson()
    {
        var parameters = (Parameters ?? new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["required"] = new JsonArray(),
        }).DeepClone();
        var fn = new JsonObject
        {
            ["name"] = Name,
            ["description"] = Description,
            ["parameters"] = parameters,
        };
        return new JsonObject { ["type"] = "function", ["function"] = fn };
    }
}

public sealed record ModelInfo(string Id, string Label, bool Free);

public sealed class StreamResult
{
    public string Content { get; set; } = "";
    public List<ToolCall>? ToolCalls { get; set; }
}