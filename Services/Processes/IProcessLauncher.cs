using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Ultron.Services.Processes;

/// <summary>
/// A process ULTRON started, or is looking for. Abstracted so the app tool's
/// undo logic (which terminates processes) can be tested without spawning or
/// killing anything real.
/// </summary>
public interface IProcessHandle
{
    int ProcessId { get; }
    DateTime StartedUtc { get; }
    bool IsRunning { get; }
    void KillTree();
}

/// <summary>
/// Starts and finds processes. Kept behind an interface because the only caller
/// that matters is destructive: the "close the app I opened" undo.
/// </summary>
public interface IProcessLauncher
{
    IProcessHandle? Start(string executable);
    IReadOnlyList<IProcessHandle> FindByImageName(string imageName);
}

public sealed class SystemProcessHandle : IProcessHandle
{
    private readonly Process _process;

    public SystemProcessHandle(Process process) => _process = process;

    public int ProcessId
    {
        get
        {
            try { return _process.Id; }
            catch (InvalidOperationException) { return -1; }
        }
    }

    public DateTime StartedUtc
    {
        get
        {
            try { return _process.StartTime.ToUniversalTime(); }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // Access is denied for processes we do not own. Assume "very old"
                // so the caller refuses to kill something it cannot vouch for.
                return DateTime.MinValue;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            try { return !_process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public void KillTree()
    {
        _process.Kill(entireProcessTree: true);
        _process.Dispose();
    }
}

public sealed class SystemProcessLauncher : IProcessLauncher
{
    public IProcessHandle? Start(string executable)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            // ShellExecute=false hands back a real process handle, which is what
            // makes a PID-accurate undo possible.
            UseShellExecute = false,
        };
        // Process.Start returns null when the process could not be started, which
        // the caller reports as a launch failure rather than a crash.
        var process = Process.Start(info);
        return process is null ? null : new SystemProcessHandle(process);
    }

    public IReadOnlyList<IProcessHandle> FindByImageName(string imageName)
    {
        if (string.IsNullOrWhiteSpace(imageName)) return Array.Empty<IProcessHandle>();
        var found = new List<IProcessHandle>();
        Process[] processes;
        try { processes = Process.GetProcessesByName(imageName); }
        catch (ArgumentException) { return Array.Empty<IProcessHandle>(); }

        foreach (var process in processes)
            found.Add(new SystemProcessHandle(process));
        return found;
    }
}
