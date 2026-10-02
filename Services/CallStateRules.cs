// CallStateRules.cs - pure call-state interpretation
// Kept WinUI-free so the rules about which states are live, which are terminal,
// and how a target name is pulled out of the backend's message can be unit
// tested. The call window only renders what these helpers decide.

namespace Ultron.Services;

public static class CallStateRules
{
    /// <summary>States where the call is over and the window should show a
    /// summary rather than live controls.</summary>
    public static bool IsTerminal(string? state) =>
        Normalize(state) is "ended" or "error" or "unavailable";

    /// <summary>States where someone is actually on the line. Ringing is
    /// excluded on purpose: the other side has not answered yet, so counting
    /// that time as call time would be a lie.</summary>
    public static bool IsLive(string? state) =>
        Normalize(state) is "connected" or "remote_speech" or "local_speech";

    /// <summary>True for the state that begins a new outgoing call, where the
    /// elapsed counter must reset to zero.</summary>
    public static bool StartsNewCall(string? state) => Normalize(state) == "ringing";

    /// <summary>Extracts the target's name from the backend message.
    /// The backend sends "Calling &lt;name&gt;..." when ringing; every other
    /// state carries no name, so callers keep whatever the last ring told them
    /// rather than overwriting a good name with a sentence.</summary>
    public static bool TryExtractTarget(string? state, string? message, out string target)
    {
        target = "";
        if (!StartsNewCall(state)) return false;

        var text = (message ?? "").Trim();
        const string prefix = "Calling";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var name = text[prefix.Length..].Trim().TrimEnd('.', '!', ' ').Trim();
        if (name.Length == 0) return false;

        target = name;
        return true;
    }

    private static string Normalize(string? state) => (state ?? "").Trim().ToLowerInvariant();
}
