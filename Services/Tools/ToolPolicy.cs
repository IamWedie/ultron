namespace Ultron.Services;

/// <summary>
/// How much damage a tool can do if the model gets it wrong or is manipulated.
/// </summary>
public enum RiskLevel
{
    /// <summary>Observes without changing anything (read a file, list windows).</summary>
    Read = 0,

    /// <summary>Reversible, local, everyday action (open an app, search the web).</summary>
    Low = 1,

    /// <summary>Visible side effects on the user's machine (type text, write a file).</summary>
    Medium = 2,

    /// <summary>Destructive or outward-facing (delete a file, send a message).</summary>
    High = 3,

    /// <summary>Irreversible or system-wide (shutdown, shell execution).</summary>
    Critical = 4,
}

public enum PolicyOutcome
{
    /// <summary>Run it.</summary>
    Allow = 0,

    /// <summary>Ask the user first.</summary>
    RequireApproval = 1,

    /// <summary>Refuse outright; the user is not offered the choice.</summary>
    Deny = 2,
}

/// <summary>The verdict for one tool call, plus the reasoning shown to the user.</summary>
public sealed record PolicyDecision(PolicyOutcome Outcome, RiskLevel Risk, string Summary)
{
    public bool NeedsApproval => Outcome == PolicyOutcome.RequireApproval;

    public static PolicyDecision Allow(RiskLevel risk, string summary) =>
        new(PolicyOutcome.Allow, risk, summary);

    public static PolicyDecision RequireApproval(RiskLevel risk, string summary) =>
        new(PolicyOutcome.RequireApproval, risk, summary);

    public static PolicyDecision Deny(RiskLevel risk, string summary) =>
        new(PolicyOutcome.Deny, risk, summary);
}

/// <summary>
/// Decides whether a call may run. Kept separate from execution so the rules can
/// be read, tested and changed in one place instead of being scattered through
/// the call site.
/// </summary>
public interface IActionPolicy
{
    PolicyDecision Evaluate(ToolCall toolCall);
}

/// <summary>
/// Asks the human. Implemented by the UI layer (a dialog); the router and every
/// tool service depend on this interface, never on the window itself.
/// </summary>
public interface IApprovalService
{
    Task<bool> RequestApprovalAsync(ToolCall toolCall, PolicyDecision decision, CancellationToken cancellationToken);
}
