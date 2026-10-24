using System.Runtime.InteropServices;

namespace Ultron.Services.Tools.Phone;

/// <summary>
/// Locates the adb executable so the phone bridge can run. Resolution order:
/// (1) explicit path configured in Settings, (2) PATH entries, (3) well-known
/// install locations. Pure-ish logic with injectable search inputs so it can be
/// unit-tested without a real device or a real PATH.
/// </summary>
public static class PhoneProbe
{
    /// <summary>Where platform-tools/adb typically lands after a manual install.</summary>
    public static readonly string[] DefaultCommonDirs =
    [
        @"C:\platform-tools",
        @"C:\adb",
        @"C:\Android\platform-tools",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools"),
    ];

    private static readonly string[] ExecutableNames = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? ["adb.exe", "adb.bat", "adb"]
        : ["adb"];

    /// <summary>
    /// Returns the first existing adb executable honoring configured-path →
    /// PATH → common dirs. Empty string when adb cannot be found.
    /// </summary>
    public static string ResolveAdb(string configuredPath, string pathEnv, IReadOnlyList<string> commonDirs)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        foreach (var dir in SplitPath(pathEnv))
            foreach (var name in ExecutableNames)
            {
                var candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }

        foreach (var dir in commonDirs)
            foreach (var name in ExecutableNames)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }

        return "";
    }

    /// <summary>Human guidance shown when no adb executable can be found.</summary>
    public static string MissingMessage() =>
        "adb was not found. Install Android platform-tools (https://developer.android.com/tools/releases/platform-tools) " +
        "and either add adb.exe to PATH or set its path in Settings → Phone.";

    private static IEnumerable<string> SplitPath(string pathEnv) =>
        (pathEnv ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(d => Directory.Exists(d));
}