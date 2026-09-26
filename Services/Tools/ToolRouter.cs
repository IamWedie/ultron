namespace Ultron.Services;

/// <summary>
/// The single entry point for every tool the model asks for.
/// </summary>
/// <remarks>
/// The order here is the security boundary:
/// <code>
/// Gemini -> ToolRouter -> policy -> approval -> IToolHandler -> OS
/// </code>
/// The router is also the only place that turns a handler outcome (or a thrown
/// exception) into the one <see cref="ToolResult"/> shape the rest of the system
/// understands, so no handler has to invent its own error protocol.
/// </remarks>
public sealed class ToolRouter
{
    private readonly Dictionary<string, IToolHandler> _handlers;
    private readonly IActionPolicy _policy;
    private readonly IApprovalService _approvals;

    public ToolRouter(IEnumerable<IToolHandler> handlers, IActionPolicy policy, IApprovalService approvals)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));

        _handlers = new Dictionary<string, IToolHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in handlers)
        {
            if (handler is null) continue;
            if (_handlers.ContainsKey(handler.Name))
                throw new ArgumentException($"Two handlers claim the tool '{handler.Name}'.", nameof(handlers));
            _handlers[handler.Name] = handler;
        }
    }

    /// <summary>Tool names this router can actually run.</summary>
    public IReadOnlyCollection<string> ToolNames => _handlers.Keys;

    public async Task<ToolResult> ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ToolResult.Cancelled("Cancelled before the tool started.");

        // 1. Resolve. An unknown tool is refused, never passed through.
        if (!_handlers.TryGetValue(toolCall.Name, out var handler))
            return ToolResult.Fail($"Unknown tool: {toolCall.Name}", ToolError.UnknownTool);

        // 2. Policy.
        PolicyDecision decision;
        try
        {
            decision = _policy.Evaluate(toolCall);
        }
        catch (Exception ex)
        {
            // A broken policy must not become an open door.
            return ToolResult.Fail($"Policy check failed, refusing to run {toolCall.Name}: {ex.Message}", ToolError.Denied);
        }

        if (decision.Outcome == PolicyOutcome.Deny)
            return ToolResult.Denied(decision.Summary);

        // 3. Approval.
        if (decision.NeedsApproval)
        {
            bool approved;
            try
            {
                approved = await _approvals.RequestApprovalAsync(toolCall, decision, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return ToolResult.Cancelled($"Approval for {toolCall.Name} was cancelled.");
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Could not ask for approval, refusing to run {toolCall.Name}: {ex.Message}", ToolError.Denied);
            }

            if (!approved)
                return ToolResult.Denied($"User declined {toolCall.Name}.");
        }

        // 4. Execute, translating every outcome into a ToolResult.
        try
        {
            var result = await handler.ExecuteAsync(toolCall, cancellationToken);
            return result ?? ToolResult.Fail($"{toolCall.Name} returned no result.", ToolError.Failed);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Cancelled($"{toolCall.Name} was cancelled.");
        }
        catch (ToolArgumentException ex)
        {
            return ToolResult.InvalidArguments(ex.Message);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"{toolCall.Name} failed: {ex.Message}", ToolError.Failed);
        }
    }
}
