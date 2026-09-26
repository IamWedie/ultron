using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Ultron.Services.Apps;

/// <summary>
/// Turns a human app name ("notepad") into something launchable. Injectable so
/// the app tool never has to know how discovery works.
/// </summary>
public interface IAppResolver
{
    Task<string?> ResolveAsync(string appName);
    void Invalidate();
}

/// <summary>
/// Builds a name -> path index from registry App Paths, Start-menu shortcuts and
/// common install locations, then caches it for an hour to avoid rescanning the
/// filesystem on every launch request.
/// </summary>
public sealed class AppResolver : IAppResolver
{
    private readonly object _lock = new();
    private readonly List<string> _startMenuDirectories;
    private readonly List<string> _installRoots;
    private readonly bool _useRegistry;
    private readonly TimeSpan _refreshInterval;

    private Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _indexBuilt;
    private DateTime _lastRefresh = DateTime.MinValue;

    public AppResolver(
        IEnumerable<string>? startMenuDirectories = null,
        IEnumerable<string>? installRoots = null,
        bool useRegistry = true,
        TimeSpan? refreshInterval = null)
    {
        _startMenuDirectories = startMenuDirectories?.ToList() ?? new List<string>
        {
            SafeFolder(Environment.SpecialFolder.StartMenu),
            SafeFolder(Environment.SpecialFolder.CommonStartMenu),
        };
        _installRoots = installRoots?.ToList() ?? new List<string>
        {
            SafeFolder(Environment.SpecialFolder.ProgramFiles),
            SafeFolder(Environment.SpecialFolder.ProgramFilesX86),
            SafeFolder(Environment.SpecialFolder.LocalApplicationData),
        };
        _useRegistry = useRegistry;
        _refreshInterval = refreshInterval ?? TimeSpan.FromHours(1);
    }

    private static string SafeFolder(Environment.SpecialFolder folder)
    {
        try { return Environment.GetFolderPath(folder); }
        catch (PlatformNotSupportedException) { return string.Empty; }
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _indexBuilt = false;
            _lastRefresh = DateTime.MinValue;
        }
    }

    public async Task<string?> ResolveAsync(string appName)
    {
        var name = Normalize(appName);
        if (string.IsNullOrWhiteSpace(name)) return null;

        lock (_lock)
        {
            if (_cache.TryGetValue(name, out var cached)) return cached;
        }

        await EnsureIndexAsync().ConfigureAwait(false);

        lock (_lock)
        {
            if (_cache.TryGetValue(name, out var cached)) return cached;
        }

        // A name that was never installed is not in the index; a direct registry
        // probe is cheap and catches apps registered after the last refresh.
        return _useRegistry ? TryRegistryLookup(name) : null;
    }

    internal static string Normalize(string? appName)
    {
        if (appName is null) return string.Empty;
        var name = appName.Trim().TrimEnd('.');
        return Path.IsPathRooted(name) ? string.Empty : name;
    }

    private async Task EnsureIndexAsync()
    {
        bool shouldRefresh;
        lock (_lock)
        {
            shouldRefresh = !_indexBuilt || (DateTime.Now - _lastRefresh) > _refreshInterval;
        }

        if (!shouldRefresh) return;

        await Task.Run(BuildIndex).ConfigureAwait(false);
    }

    private void BuildIndex()
    {
        var newCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (_useRegistry) IndexRegistryAppPaths(newCache);
        IndexStartMenuShortcuts(newCache);
        IndexInstallRoots(newCache);

        lock (_lock)
        {
            _cache = newCache;
            _indexBuilt = true;
            _lastRefresh = DateTime.Now;
        }
    }

    private static void IndexRegistryAppPaths(Dictionary<string, string> cache)
    {
        try
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                using var appsKey = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
                if (appsKey == null) continue;

                foreach (var sub in appsKey.GetSubKeyNames())
                {
                    if (!sub.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        using var subKey = appsKey.OpenSubKey(sub);
                        var value = subKey?.GetValue(null)?.ToString();
                        AddIfUsable(cache, Path.GetFileNameWithoutExtension(sub), value);
                    }
                    catch { /* a single unreadable entry is not fatal */ }
                }
            }
        }
        catch { /* registry unavailable (non-Windows, permissions) */ }
    }

    private void IndexStartMenuShortcuts(Dictionary<string, string> cache)
    {
        foreach (var dir in _startMenuDirectories)
        {
            if (string.IsNullOrEmpty(dir) || !SafeDirectoryExists(dir)) continue;
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                    AddIfUsable(cache, Path.GetFileNameWithoutExtension(lnk), lnk);
            }
            catch { }
        }
    }

    private void IndexInstallRoots(Dictionary<string, string> cache)
    {
        foreach (var root in _installRoots)
        {
            if (string.IsNullOrEmpty(root) || !SafeDirectoryExists(root)) continue;
            try
            {
                // Two levels deep keeps startup cost bounded.
                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        foreach (var exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
                            AddIfUsable(cache, Path.GetFileNameWithoutExtension(exe), exe);

                        foreach (var subDir in Directory.EnumerateDirectories(dir))
                            foreach (var exe in Directory.EnumerateFiles(subDir, "*.exe", SearchOption.TopDirectoryOnly))
                                AddIfUsable(cache, Path.GetFileNameWithoutExtension(exe), exe);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private static void AddIfUsable(Dictionary<string, string> cache, string? stem, string? path)
    {
        if (string.IsNullOrWhiteSpace(stem) || string.IsNullOrWhiteSpace(path)) return;
        if (!cache.ContainsKey(stem!)) cache[stem!] = path!;
    }

    private static bool SafeDirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static string? TryRegistryLookup(string name)
    {
        try
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                using var appsKey = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
                if (appsKey == null) continue;

                using var subKey = appsKey.OpenSubKey(name + ".exe");
                var value = subKey?.GetValue(null)?.ToString();
                if (!string.IsNullOrEmpty(value) && File.Exists(value)) return value;
            }
        }
        catch { }
        return null;
    }
}
