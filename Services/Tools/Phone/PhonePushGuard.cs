using System.Text.Json.Nodes;

namespace Ultron.Services.Tools.Phone;

/// <summary>
/// Validates the PC-side source of a <c>phone_push_file</c> call before it is
/// handed to <c>adb push</c>. File tools are confined to the user profile by
/// <see cref="IPathPolicy"/>; the phone bridge must enforce the same rule or a
/// manipulated model could copy private files onto the phone.
/// </summary>
public static class PhonePushGuard
{
    public static ToolResult ResolveLocal(JsonNode? args, IPathPolicy policy)
    {
        if (args is not JsonObject obj)
            return ToolResult.InvalidArguments("phone_push_file needs a JSON object with a 'local' path.");

        var raw = obj["local"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
        var verdict = policy.Validate(raw);
        if (!verdict.Allowed)
            return ToolResult.Fail(verdict.Error, ToolError.Denied);

        if (!File.Exists(verdict.FullPath))
            return ToolResult.NotFound($"Local file not found: {verdict.FullPath}");

        // Hand the canonical path to adb: a relative path would otherwise be
        // resolved against the app's working directory, not the validated file.
        obj["local"] = verdict.FullPath;
        return ToolResult.Ok(verdict.FullPath);
    }
}
