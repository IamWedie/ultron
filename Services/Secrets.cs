using System.Text.RegularExpressions;

namespace Ultron.Services;

/// <summary>Pattern-based secret scrubber shared by the log pipeline and the
/// memory store so API keys/tokens never hit disk in plaintext.</summary>
public static partial class Secrets
{
    [GeneratedRegex("AIza[0-9A-Za-z_\\-]{20,}", RegexOptions.Compiled)]
    private static partial Regex GeminiKey();

    [GeneratedRegex("AQ\\.[0-9A-Za-z_\\-]{20,}", RegexOptions.Compiled)]
    private static partial Regex OauthToken();

    [GeneratedRegex("sk-[0-9A-Za-z]{20,}", RegexOptions.Compiled)]
    private static partial Regex SkKey();

    [GeneratedRegex("Bearer [0-9A-Za-z._\\-]{16,}", RegexOptions.Compiled)]
    private static partial Regex Bearer();

    [GeneratedRegex("\\b[0-9a-f]{64}\\b", RegexOptions.Compiled)]
    private static partial Regex LongHex();

    public static string Redact(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s ?? "";
        s = GeminiKey().Replace(s, "***REDACTED***");
        s = OauthToken().Replace(s, "***REDACTED***");
        s = SkKey().Replace(s, "***REDACTED***");
        s = Bearer().Replace(s, "***REDACTED***");
        s = LongHex().Replace(s, "***REDACTED***");
        return s;
    }
}