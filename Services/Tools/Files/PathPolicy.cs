namespace Ultron.Services;

/// <summary>Verdict from a path check: either an absolute path, or why it was refused.</summary>
public readonly record struct PathVerdict(bool Allowed, string FullPath, string Error)
{
    public static PathVerdict Ok(string fullPath) => new(true, fullPath, "");

    public static PathVerdict Deny(string error) => new(false, "", error);
}

/// <summary>
/// Decides which paths the assistant may touch. Extracted so the rule is stated
/// once, testable, and impossible to bypass by forgetting a check in one handler.
/// </summary>
public interface IPathPolicy
{
    /// <summary>Canonical path of the permitted root, with a trailing separator.</summary>
    string Root { get; }

    PathVerdict Validate(string? rawPath);
}

/// <summary>
/// Confines file operations to the signed-in user's own profile directory.
/// </summary>
/// <remarks>
/// Everything the assistant writes lives under the profile anyway, so this costs
/// nothing in practice while removing the ability to touch C:\Windows, another
/// user's Documents, or a network share.
/// </remarks>
public sealed class UserProfilePathPolicy : IPathPolicy
{
    private readonly string _root;
    private readonly string _rootWithoutSeparator;

    public UserProfilePathPolicy(string? root = null)
    {
        var profile = root ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
            throw new InvalidOperationException("The user profile directory could not be resolved.");
        _rootWithoutSeparator = Path.GetFullPath(profile)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _root = _rootWithoutSeparator + Path.DirectorySeparatorChar;
    }

    public string Root => _root;

    /// <summary>Characters Windows forbids in a path, plus control characters.</summary>
    private static readonly char[] Forbidden = { '<', '>', '|', '"', '?', '*' };

    public PathVerdict Validate(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return PathVerdict.Deny("A path is required.");

        var raw = rawPath.Trim();

        // Reject the shapes that escape a prefix check before normalizing.
        if (raw.StartsWith(@"\\", StringComparison.Ordinal) ||   // UNC
            raw.StartsWith("//", StringComparison.Ordinal) ||
            raw.StartsWith(@"\\?\", StringComparison.Ordinal) ||  // device
            raw.StartsWith(@"\\.\", StringComparison.Ordinal))  // device
        {
            return PathVerdict.Deny("UNC and device paths are not allowed.");
        }

        // GetFullPath no longer rejects these, and they can only ever produce a
        // confusing failure deep inside a file operation.
        if (raw.IndexOfAny(Forbidden) >= 0 || raw.Any(char.IsControl))
            return PathVerdict.Deny("The path contains characters that are not allowed.");

        string full;
        try
        {
            full = Path.GetFullPath(raw);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return PathVerdict.Deny("Invalid path.");
        }

        // Compare canonical paths: "C:\Users\me\..\Windows" must not pass a
        // prefix check performed on the raw string. The root itself is in scope
        // (listing your home folder is legitimate); refusing to *destroy* it is
        // the caller's job, not the scope check's.
        //
        // The root is compared both with and without its trailing separator:
        // GetFullPath preserves one when the input had it, so a plain prefix test
        // would accept "C:\me\" but reject "C:\me".
        if (string.Equals(full, _rootWithoutSeparator, StringComparison.OrdinalIgnoreCase))
            return PathVerdict.Ok(full);

        // _root keeps its separator, so a sibling such as "C:\me-backup" cannot
        // masquerade as a child.
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            return PathVerdict.Deny("File operations are limited to files inside your user profile.");

        return PathVerdict.Ok(full);
    }
}
