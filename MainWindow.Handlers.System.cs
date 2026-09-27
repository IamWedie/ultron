// MainWindow.Handlers.System.cs - system_status, computer_settings
// Part of MainWindow. Fields and composition root live in MainWindow.xaml.cs.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Ultron.Services;
using Ultron.Services.Apps;
using Ultron.Services.Processes;
using Ultron.Services.Tools.Apps;
using Windows.System;
using Windows.UI;
using Microsoft.UI;
using Xaml = Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Buffers;
using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using QRCoder;

namespace Ultron;

public sealed partial class MainWindow
{
    private string HandleSystemStatus()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var mem = proc.WorkingSet64 / 1024 / 1024;
        var totalThreads = 0;
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try { totalThreads += p.Threads.Count; } catch { }
        }
        return $"Memory: {mem} MB, Threads across system: {totalThreads}, Uptime: {DateTime.Now - proc.StartTime:hh\\:mm\\:ss}";
    }

    private async Task<string> HandleComputerSettings(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
        switch (action.ToLowerInvariant())
        {
            case "volume_up":
            case "volume_down":
            case "mute":
            case "unmute":
            {
                var before = SystemVolume.Get();
                string result;
                switch (action.ToLowerInvariant())
                {
                    case "volume_up": result = await RunAsync("nircmd.exe", "changesysvolume 2000"); break;
                    case "volume_down": result = await RunAsync("nircmd.exe", "changesysvolume -2000"); break;
                    case "mute": result = await RunAsync("nircmd.exe", "mutesysvolume 1"); break;
                    default: result = await RunAsync("nircmd.exe", "mutesysvolume 0"); break;
                }
                // Undo restores the EXACT prior volume + mute state. If we could
                // not read the value, register nothing — undoing a guess is worse.
                if (before.volume >= 0f)
                {
var (v, m) = before;
                    PushUndo($"computer_settings({action})", () =>
                    {
                        SystemVolume.Set(v, m);
                        var pct = (int)(v * 100f);
                        var suffix = m ? " (muted)" : "";
                        return $"Restored volume to {pct}%{suffix}.";
                    });
                }
                return result;
            }
            case "shutdown": return await RunAsync("shutdown", "/s /t 30");
            case "restart": return await RunAsync("shutdown", "/r /t 30");
            case "sleep": return await RunAsync("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
            case "lock": return await RunAsync("rundll32.exe", "user32.dll,LockWorkStation");
            case "screenshot": return await RunAsync("snippingtool", "/clip");
            default: return $"Unknown action: {action}";
        }
    }

    private async Task<string> RunAsync(string file, string args)
    {
        var result = await _commandRunner.RunAsync(file, args);
        return result.Describe(file);
    }

}
