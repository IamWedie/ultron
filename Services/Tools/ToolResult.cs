using System.Text.Json;

namespace Ultron.Services;

/// <summary>
/// Machine-readable reason a tool call did not succeed. Sent to the model as a
/// stable token so behaviour can be reasoned about without parsing prose.
/// </summary>
public enum ToolError
{
    None = 0,
    UnknownTool,
    InvalidArguments,
    Denied,
    NotFound,
    Unsupported,
    Failed,
    TimedOut,
    Cancelled,
}

/// <summary>
/// The outcome of a single tool invocation.
/// </summary>
/// <remarks>
/// One shape for every outcome - success, denial, bad arguments and crash all
/// arrive as a <see cref="ToolResult"/>, so the router, the audit log and the
/// model-facing wire format never have to special-case a string.
/// </remarks>
public sealed record ToolResult
{
    private ToolResult(bool success, string message, ToolError error, JsonElement? data)
    {
        Success = success;
        Message = message;
        Error = error;
        Data = data;
    }

    public bool Success { get; }

    /// <summary>Human-readable summary; this is what the model reads.</summary>
    public string Message { get; }

    public ToolError Error { get; }

    /// <summary>Optional structured payload, already serialized and detached.</summary>
    public JsonElement? Data { get; }

    public static ToolResult Ok(string message, object? data = null) =>
        new(true, message, ToolError.None, ToElement(data));

    public static ToolResult Fail(string message, ToolError error = ToolError.Failed) =>
        new(false, message, error, null);

    public static ToolResult InvalidArguments(string message) =>
        new(false, message, ToolError.InvalidArguments, null);

    /// <summary>The user (or policy) refused the action. Not an error to report loudly.</summary>
    public static ToolResult Denied(string message) =>
        new(false, message, ToolError.Denied, null);

    public static ToolResult NotFound(string message) =>
        new(false, message, ToolError.NotFound, null);

    public static ToolResult Unsupported(string message) =>
        new(false, message, ToolError.Unsupported, null);

    public static ToolResult Cancelled(string message) =>
        new(false, message, ToolError.Cancelled, null);

    private static JsonElement? ToElement(object? data)
    {
        if (data is null) return null;
        try
        {
            return JsonSerializer.SerializeToElement(data);
        }
        catch (Exception)
        {
            // Deliberately broad: a diagnostic payload (cycle, unsupported member,
            // depth) must never be the reason a tool result is lost. The result
            // itself is what matters; the payload is a bonus.
            return null;
        }
    }

    /// <summary>
    /// The exact string handed to the model. Failures are tagged so the model can
    /// tell "the action was refused" from "the action ran and said no".
    /// </summary>
    public string ToModelString() =>
        Success ? Message : $"ERROR [{Error}]: {Message}";

    public override string ToString() => ToModelString();
}
