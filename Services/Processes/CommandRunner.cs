using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ultron.Services.Processes;

public sealed record CommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Started)
{
    public bool Succeeded => Started && !TimedOut && ExitCode == 0;

    /// <summary>stdout if there is any, otherwise stderr: the useful half of a console tool's output.</summary>
    public string Combined =>
        !string.IsNullOrWhiteSpace(StandardOutput) ? StandardOutput.Trim()
        : !string.IsNullOrWhiteSpace(StandardError) ? StandardError.Trim()
        : string.Empty;

    /// <summary>
    /// A sentence for the model. A non-zero exit is reported as a failure: the old
    /// code returned "Exit code: 1" as if it were a normal result, so a refused
    /// mute or a failed shutdown read like a success.
    /// </summary>
    public string Describe(string command)
    {
        if (!Started) return $"Failed: could not start {command}. {StandardError}".TrimEnd();
        if (TimedOut) return $"Failed: {command} did not finish in time and was stopped.";
        var body = Combined.Length == 0 ? "No output." : Combined;
        return Succeeded
            ? $"Exit code: {ExitCode}. {body}"
            : $"Failed: {command} exited with code {ExitCode}. {body}";
    }
}

/// <summary>
/// Runs a short-lived console tool and reports its real outcome.
/// </summary>
public sealed class CommandRunner
{
    /// <summary>Console output is for a model to read, not to be flooded by.</summary>
    public const int MaxOutputChars = 8_000;

    public TimeSpan DefaultTimeout { get; }

    public CommandRunner(TimeSpan? defaultTimeout = null) =>
        DefaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);

    public async Task<CommandResult> RunAsync(
        string file, string arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var effective = timeout ?? DefaultTimeout;

        var info = new ProcessStartInfo(file, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = info };
        var started = false;
        try
        {
            started = process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
        {
            return new CommandResult(-1, string.Empty, ex.Message, false, false);
        }

        if (!started)
            return new CommandResult(-1, string.Empty, $"Could not start {file}.", false, false);

        // Drain both pipes concurrently and BEFORE waiting for exit. Waiting first
        // deadlocks as soon as the child fills the OS pipe buffer: the child blocks
        // writing, the parent blocks waiting, and neither ever moves again.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var timedOut = false;
        {
            // WaitForExitAsync has no timeout overload, so race it against one.
            // Racing also tells a timeout apart from a caller cancellation: the
            // delay completes either way, the exit task only completes on exit.
            var exitTask = process.WaitForExitAsync(CancellationToken.None);
            var deadline = Task.Delay(effective, cancellationToken);
            var finished = await Task.WhenAny(exitTask, deadline).ConfigureAwait(false);
            timedOut = !ReferenceEquals(finished, exitTask);
        }

        if (timedOut)
        {
            TryKill(process);
            // Give the readers a moment to drain whatever the child already wrote
            // so a timeout still reports useful context.
            await Task.WhenAny(
                Task.WhenAll(stdoutTask, stderrTask),
                Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)).ConfigureAwait(false);
        }
        else
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }

        var stdout = Truncate(Await(stdoutTask));
        var stderr = Truncate(Await(stderrTask));

        int exitCode;
        try { exitCode = process.ExitCode; }
        catch (InvalidOperationException) { exitCode = timedOut ? -1 : 0; }

        return new CommandResult(exitCode, stdout, stderr, timedOut, true);
    }

    private static string Await(Task<string> task)
    {
        try { return task.GetAwaiter().GetResult(); }
        catch { return string.Empty; }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone, or we are not allowed to kill it.
        }
    }

    private static string Truncate(string value) =>
        value.Length <= MaxOutputChars
            ? value
            : value[..MaxOutputChars] + $"\n... (truncated, {value.Length} characters total)";
}
