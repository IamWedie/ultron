using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ultron.Services.Tools.Phone;

/// <summary>
/// The bridge to a physical phone over ADB, used by <see cref="PhoneToolService"/>.
/// A dedicated seam so the router can be tested with a fake and a real device
/// only ever touches <see cref="AdbPhone"/>.
/// </summary>
public interface IAdbPhone
{
    ToolResult Execute(string name, JsonNode? args);
}

/// <summary>
/// Real ADB-backed implementation of <see cref="IAdbPhone"/>.
/// </summary>
/// <remarks>
/// Failure policy: a phone operation either proves it ran (non-empty, error-free
/// output) or it is reported as a failure. The old <see cref="PhoneTools"/>
/// layer returned prose strings and swallowed errors; this wrapper turns a
/// missing/offline device and any adb error into a machine-readable
/// <see cref="ToolResult"/>, so the model never sees "done" for nothing.
/// </remarks>
public sealed class AdbPhone : IAdbPhone
{
    private readonly AdbClient _client;
    private readonly IPathPolicy _pathPolicy;

    public AdbPhone(AdbClient client, IPathPolicy? pathPolicy = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _pathPolicy = pathPolicy ?? new UserProfilePathPolicy();
    }

    public ToolResult Execute(string name, JsonNode? args)
    {
        // `phone_push_file` reads a PC file. Confine it with the same rule the
        // file tools use, before the call can reach adb.
        if (name == "phone_push_file")
        {
            var verdict = PhonePushGuard.ResolveLocal(args, _pathPolicy);
            if (!verdict.Success) return verdict;
        }

        if (!_client.Connect())
            return ToolResult.Fail(
                string.IsNullOrEmpty(_client.LastError) ? "Phone not reachable over ADB." : _client.LastError);

        var output = PhoneTools.Execute(_client, name, args);

        if (!string.IsNullOrEmpty(_client.LastError))
            return ToolResult.Fail(_client.LastError, ToolError.Failed);

        if (string.IsNullOrWhiteSpace(output))
            return ToolResult.Fail($"{name} produced no output.", ToolError.Failed);

        return ToolResult.Ok(output);
    }
}

/// <summary>
/// Stand-in used when the bridge is disabled or adb is not installed. The model
/// always sees the declared phone tools, so those calls must answer with the
/// reason instead of surfacing "Unknown tool".
/// </summary>
public sealed class UnavailablePhone : IAdbPhone
{
    private readonly string _reason;

    public UnavailablePhone(string reason) =>
        _reason = string.IsNullOrWhiteSpace(reason) ? "Phone control is not available." : reason;

    public ToolResult Execute(string name, JsonNode? args) => ToolResult.Fail(_reason, ToolError.Unsupported);
}

/// <summary>
/// Exposes the curated <c>phone_*</c> tools to the tool router.
/// </summary>
/// <remarks>
/// The full catalog is ~122 tools; only the <see cref="PhoneTools.Curated"/>
/// subset is exposed and anything else is refused. Each curated tool gets its
/// own <see cref="IToolHandler"/> (the router maps one handler per name), all
/// backed by a single <see cref="IAdbPhone"/>.
/// </remarks>
public sealed class PhoneToolService
{
    private static readonly HashSet<string> CuratedNames =
        new(PhoneTools.Curated.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

    private readonly IAdbPhone _phone;

    public PhoneToolService(IAdbPhone phone) => _phone = phone ?? throw new ArgumentNullException(nameof(phone));

    /// <summary>One handler per curated tool, ready to register in the router.</summary>
    public IEnumerable<IToolHandler> Handlers =>
        PhoneTools.Curated.Select(t => new DelegateToolHandler(t.Name, ExecuteAsync));

    /// <summary>Executes a phone tool, refusing anything outside the curated set.</summary>
    public ToolResult ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken)
    {
        if (!CuratedNames.Contains(toolCall.Name))
            return ToolResult.Unsupported($"Tool '{toolCall.Name}' is not in the curated phone tool set.");

        JsonNode? args = toolCall.Arguments.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(toolCall.Arguments.GetRawText())
            : null;

        return _phone.Execute(toolCall.Name, args);
    }
}
