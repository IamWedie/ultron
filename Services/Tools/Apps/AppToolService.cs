using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Ultron.Services.Apps;
using Ultron.Services.Processes;
using Ultron.Services.Tools;

namespace Ultron.Services.Tools.Apps;

public sealed class AppToolArgs
{
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("app_name")] public string? AppName { get; set; }
}

/// <summary>
/// Brings a newly launched window forward. Implemented by the UI layer because
/// it needs Win32 window handles; the service stays Win32-free and testable.
/// </summary>
public interface ILaunchedWindowActivator
{
    /// <summary>
    /// Waits for the launched app's main window, focuses it, and returns its
    /// handle. Implementations must await rather than sleep, because the caller
    /// may be on the UI thread.
    /// </summary>
    Task<IntPtr> ActivateAsync(string imageName, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Launches registered desktop applications and registers a PID-accurate undo.
/// </summary>
public sealed class AppToolService : IToolHandler
{
    public const string ToolName = "open_app";

    private static readonly TimeSpan ActivationWait = TimeSpan.FromSeconds(4);

    private readonly IAppResolver _resolver;
    private readonly IProcessLauncher _launcher;
    private readonly ILaunchedWindowActivator _activator;
    private readonly UndoLedger _undo;

    public AppToolService(
        IAppResolver resolver,
        IProcessLauncher launcher,
        ILaunchedWindowActivator activator,
        UndoLedger undo)
    {
        _resolver = resolver;
        _launcher = launcher;
        _activator = activator;
        _undo = undo;
    }

    public string Name => ToolName;

    public Task<ToolResult> ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken)
    {
        var args = toolCall.Deserialize<AppToolArgs>();
        var action = (args.Action ?? "open").Trim().ToLowerInvariant();

        return action switch
        {
            "open" or "launch" or "start" => OpenAsync(args.AppName, cancellationToken),
            _ => Task.FromResult(ToolResult.InvalidArguments(
                $"open_app: unknown action '{action}'. Use 'open'.")),
        };
    }

    private async Task<ToolResult> OpenAsync(string? rawName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return ToolResult.InvalidArguments("open_app: missing app_name.");

        var appName = rawName.Trim();

        // The catalog resolves bare names only. Letting a path through would turn
        // "open notepad" into "run any executable on disk".
        if (appName.Contains(Path.DirectorySeparatorChar) || appName.Contains(Path.AltDirectorySeparatorChar))
            return ToolResult.InvalidArguments("open_app: only registered application names are allowed.");

        var executable = await _resolver.ResolveAsync(appName).ConfigureAwait(false);
        if (string.IsNullOrEmpty(executable))
            return ToolResult.NotFound($"Application '{appName}' was not found in the registered app catalog.");

        IProcessHandle? launched;
        try
        {
            launched = _launcher.Start(executable);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return ToolResult.Fail($"Failed to open {appName}: {ex.Message}");
        }

        if (launched is null)
            return ToolResult.Fail($"Failed to open {appName}: the process did not start.");

        // Read the start time now, while the process is definitely alive. A dead
        // handle degrades to DateTime.MinValue, and a MinValue threshold would
        // make the name-based fallback below match every process on the machine.
        var startedUtc = launched.StartedUtc;

        var imageName = Path.GetFileNameWithoutExtension(executable);
        if (string.IsNullOrWhiteSpace(imageName)) imageName = Path.GetFileNameWithoutExtension(appName);

        // Focus the new window so "open X, then type Y" behaves as a user expects.
        var window = IntPtr.Zero;
        if (!string.IsNullOrWhiteSpace(imageName) && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                window = await _activator.ActivateAsync(imageName!, ActivationWait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ToolResult.Cancelled($"Opening {appName} was cancelled.");
            }
        }

        var activated = window != IntPtr.Zero;
        _undo.Push($"open_app({appName})", () => CloseLaunched(imageName!, launched, startedUtc));

        return ToolResult.Ok(
            $"Opened {appName} ({executable})." + (activated ? " Window focused." : " Window not detected yet."),
            new { app = appName, path = executable, process_id = launched.ProcessId, window_focused = activated });
    }

    /// <summary>
    /// The undo. Kills only what ULTRON started, verified by PID and start time.
    /// </summary>
    private string CloseLaunched(string imageName, IProcessHandle launched, DateTime launchedStartedUtc)
    {
        var closed = 0;

        if (launched.IsRunning)
        {
            try
            {
                launched.KillTree();
                closed++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Already gone.
            }
        }

        // Only if PID tracking failed do we look by name, and then only for
        // processes that started at or after the one we launched. There is
        // deliberately no "kill everything with this name" path: that would close
        // apps the user started themselves.
        //
        // If the launch-time start stamp is unknown, we cannot tell a sibling
        // from the target, so the fallback is skipped entirely.
        if (closed == 0 && launchedStartedUtc != DateTime.MinValue)
        {
            foreach (var candidate in _launcher.FindByImageName(imageName))
            {
                try
                {
                    if (candidate.StartedUtc < launchedStartedUtc) continue;
                    candidate.KillTree();
                    closed++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                }
            }
        }

        return closed > 0
            ? $"Closed {imageName} ({closed} process(es))."
            : "App is already closed or was not launched by ULTRON.";
    }
}
