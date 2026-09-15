using System.Diagnostics;

namespace Ultron.Services;

/// <summary>
/// Crash-recovery watchdog.  The main app writes a heartbeat on a timer and
/// launches a sibling guardian process (same exe, <c>--watchdog</c>).  The
/// guardian watches the heartbeat; if the main process dies without writing a
/// clean quit flag, it relaunches the app (bounded to 3 launches per 15 min)
/// and snapshots breadcrumbs for diagnosis.
/// </summary>
public static class Watchdog
{
    private const string MainArg = "--watchdog";
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ultron", "watchdog");

    private static string HeartbeatPath => Path.Combine(Dir, "heartbeat.txt");
    private static string QuitPath => Path.Combine(Dir, "quit.flag");

    private static void EnsureDir() => Directory.CreateDirectory(Dir);

    /// <summary>Called by the main app on a timer (every ~10s). Never throws.</summary>
    public static void TouchHeartbeat()
    {
        try
        {
            EnsureDir();
            File.WriteAllText(HeartbeatPath, DateTime.UtcNow.ToString("o"));
        }
        catch
        {
            // Never let a watchdog file I/O fault take down the assistant.
        }
    }

    /// <summary>Written when the user quits cleanly; the guardian then exits too.</summary>
    public static void WriteQuitFlag()
    {
        try
        {
            EnsureDir();
            File.WriteAllText(QuitPath, DateTime.UtcNow.ToString("o"));
        }
        catch
        {
        }
    }

    /// <summary>Spawns the sibling guardian unless one is already running (mutex).</summary>
    public static void LaunchGuardianIfNeeded()
    {
        bool created;
        using (new Mutex(true, "UltronWatchdogSingleton", out created))
        {
            if (created)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    try
                    {
                        EnsureDir();
                        Process.Start(new ProcessStartInfo(exe, MainArg)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden,
                        });
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    /// <summary>Guardian entry point (runs in its own process; no UI).</summary>
    public static void RunGuardian()
    {
        try
        {
            EnsureDir();
        }
        catch
        {
            return;
        }

        bool created;
        using (var mutex = new Mutex(true, "UltronWatchdogSingleton", out created))
        {
            if (!created) return;
            var relaunches = new List<DateTime>();
            var startedUtc = DateTime.UtcNow;

            while (true)
            {
                try
                {
                    if (File.Exists(QuitPath))
                    {
                        File.Delete(QuitPath);
                        break;
                    }

                    if (IsStale(startedUtc) && !IsMainAlive())
                    {
                        var now = DateTime.UtcNow;
                        relaunches.Add(now);
                        relaunches.RemoveAll(t => now - t > TimeSpan.FromMinutes(15));
                        if (relaunches.Count >= 3)
                        {
                            WriteFatalBreadcrumb(relaunches.Count);
                            break;
                        }

                        CopyBreadcrumbs();
                        TouchHeartbeat();
                        var exe = Environment.ProcessPath;
                        if (!string.IsNullOrEmpty(exe))
                            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
                    }
                }
                catch
                {
                    // Keep polling; a transient fault must not kill the guardian.
                }

                Thread.Sleep(PollInterval);
            }
        }
    }

    private static bool IsStale(DateTime guardianStart)
    {
        try
        {
            var fi = new FileInfo(HeartbeatPath);
            if (!fi.Exists)
                return DateTime.UtcNow - guardianStart > TimeSpan.FromSeconds(60);
            return DateTime.UtcNow - fi.LastWriteTimeUtc > StaleAfter;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsMainAlive()
    {
        var self = Environment.ProcessId;
        var selfPath = Environment.ProcessPath;
        try
        {
            foreach (var p in Process.GetProcessesByName("ULTRON"))
            {
                if (p.Id == self) continue;
                try
                {
                    if (string.Equals(p.MainModule?.FileName, selfPath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // Access denied on another user's process — still alive, treat as running.
                    return true;
                }
            }
        }
        catch
        {
            return true;
        }
        return false;
    }

    private static void CopyBreadcrumbs()
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dest = Path.Combine(Dir, "breadcrumbs", $"bx_{stamp}");
            Directory.CreateDirectory(dest);
            foreach (var name in new[] { "heartbeat.txt", "crash.log", "ultron.log" })
            {
                var src = Path.Combine(Path.GetDirectoryName(Dir) ?? Dir, name);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(dest, name), true);
            }
        }
        catch
        {
        }
    }

    private static void WriteFatalBreadcrumb(int relaunchCount)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dest = Path.Combine(Dir, "breadcrumbs", $"fatal_{stamp}");
            Directory.CreateDirectory(dest);
            File.WriteAllText(Path.Combine(dest, "summary.txt"),
                $"Fatal: ULTRON crashed {relaunchCount}+ times within 15 minutes.\n" +
                $"Last observed: {File.ReadAllText(HeartbeatPath)}\n" +
                $"UTC now: {DateTime.UtcNow:o}\n");
            CopyBreadcrumbs();
        }
        catch
        {
        }
    }
}