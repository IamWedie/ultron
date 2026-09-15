using System.Diagnostics;

namespace Ultron.Services;

public sealed record TelemetrySnapshot(double CpuPercent, double MemMb, double MemTotalGb, int ThreadCount, TimeSpan Uptime);

public sealed class SystemTelemetry
{
    private readonly object _lock = new();
    private Process? _proc;
    private DateTime _lastSampleAt = DateTime.UtcNow;
    private TimeSpan _lastCpuTime;

    public TelemetrySnapshot Snapshot()
    {
        double cpu;
        lock (_lock)
        {
            _proc ??= Process.GetCurrentProcess();
            _proc.Refresh();
            var now = DateTime.UtcNow;
            var delta = now - _lastSampleAt;
            _lastSampleAt = now;
            var cpuTime = _proc.TotalProcessorTime;
            var cpuDelta = (cpuTime - _lastCpuTime).TotalSeconds;
            _lastCpuTime = cpuTime;
            var cores = Math.Max(1, Environment.ProcessorCount);
            cpu = delta.TotalSeconds > 0
                ? Math.Clamp((cpuDelta / (delta.TotalSeconds * cores)) * 100.0, 0, 100)
                : 0;
        }

        var p = _proc ?? Process.GetCurrentProcess();
        var memMb = p.WorkingSet64 / 1024d / 1024d;
        var memTotalGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024d / 1024d;
        var uptime = DateTime.Now - p.StartTime;
        return new TelemetrySnapshot(cpu, memMb, memTotalGb, p.Threads.Count, uptime);
    }
}