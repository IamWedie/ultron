using System.Text.Json;

namespace Ultron.Services;

/// <summary>
/// A single tool invocation requested by the model.
/// </summary>
/// <remarks>
/// The arguments are kept as the raw <see cref="JsonElement"/> exactly as the
/// backend produced them. Flattening them into a string dictionary (the old
/// behaviour) destroyed type information - every number, boolean and nested
/// object arrived as text - which forced each handler to re-parse defensively
/// and silently treated a JSON <c>null</c> as the literal string "null".
///
/// <see cref="Arguments"/> is always detached from the caller's
/// <see cref="JsonDocument"/> (see the constructor), so a call may safely be
/// queued, awaited on, or handed to another thread.
/// </remarks>
public sealed record ToolCall
{
    public ToolCall(string id, string name, JsonElement arguments)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Tool call id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tool name is required.", nameof(name));

        Id = id;
        Name = name.Trim();
        Arguments = Detach(arguments, nameof(arguments));
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>Always an object, never null/undefined, and never tied to a live document.</summary>
    public JsonElement Arguments { get; }

    private static readonly JsonElement EmptyArguments =
        JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>
    /// Copies the element so it outlives the document that produced it, and
    /// normalises the "no arguments" cases to a real empty object.
    /// </summary>
    private static JsonElement Detach(JsonElement arguments, string paramName)
    {
        switch (arguments.ValueKind)
        {
            case JsonValueKind.Object:
                return arguments.Clone();
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                return EmptyArguments;
            default:
                throw new ArgumentException(
                    $"Tool arguments must be a JSON object, got {arguments.ValueKind}.", paramName);
        }
    }

    /// <summary>
    /// Tool arguments use snake_case on the wire (app_name, file_path,
    /// message_text) while handlers are written in PascalCase, so deserialization
    /// follows the snake_case naming policy and ignores case. That lets a handler
    /// declare <c>record OpenAppArgs(string AppName)</c> and receive
    /// <c>{"app_name":"notepad"}</c> with no attribute noise.
    /// </summary>
    private static readonly JsonSerializerOptions ArgOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// Deserializes the arguments into a typed record.
    /// </summary>
    /// <exception cref="ToolArgumentException">
    /// The payload does not match <typeparamref name="T"/>. Callers should let the
    /// router translate this into a single, consistently shaped failure result.
    /// </exception>
    public T Deserialize<T>() where T : class
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(Arguments, ArgOptions);
            if (value is null)
                throw new ToolArgumentException($"Arguments did not produce a {typeof(T).Name}.");
            return value;
        }
        catch (JsonException ex)
        {
            throw new ToolArgumentException(
                $"Arguments do not match the expected shape: {ex.Message}", ex);
        }
    }

    /// <summary>String argument, or null when absent. Non-scalars yield null.</summary>
    public string? GetString(string name) => Arguments.TryGetProperty(name, out var el)
        ? el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => el.GetRawText(),
            _ => null,
        }
        : null;

    /// <summary>
    /// Integer argument. Accepts a JSON number or a numeric string (models
    /// sometimes quote numbers); returns null for anything else.
    /// </summary>
    public int? GetInt32(string name)
    {
        if (!Arguments.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(el.GetString(), out var n) => n,
            _ => null,
        };
    }

    /// <summary>Boolean argument. Accepts a JSON bool or the strings "true"/"false".</summary>
    public bool? GetBoolean(string name)
    {
        if (!Arguments.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(el.GetString(), out var b) => b,
            _ => null,
        };
    }

    public bool Has(string name) =>
        Arguments.TryGetProperty(name, out var el) && el.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    public override string ToString() => $"{Name}({Arguments.GetRawText()})";
}

/// <summary>Tool arguments that did not match the handler's expected shape.</summary>
public sealed class ToolArgumentException : Exception
{
    public ToolArgumentException(string message) : base(message) { }
    public ToolArgumentException(string message, Exception inner) : base(message, inner) { }
}
