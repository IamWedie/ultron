using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Ultron.Services.Processes;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// The test project targets net10.0-windows, so these always run on Windows.
/// Runs real console processes, so these cover the pipe deadlock and the timeout
/// path that the previous RunCommand got wrong.
/// </summary>
public sealed class CommandRunnerTests
{

    private static string Cmd(string command) => $"/c {command}";

    [Fact]
    public async Task CapturesStdoutAndExitCode()
    {

        var result = await new CommandRunner().RunAsync("cmd.exe", Cmd("echo hello"));

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.Combined);
    }

    [Fact]
    public async Task DoesNotDeadlockOnLargeOutput()
    {
        // The regression: waiting for exit before reading the pipes hangs forever
        // once the child fills the ~4 KB pipe buffer. This writes far more than
        // that, so a reordering of these two steps fails the test by timing out.

        var result = await new CommandRunner(TimeSpan.FromSeconds(60))
            .RunAsync("cmd.exe", Cmd("for /L %i in (1,1,5000) do @echo 0123456789012345678901234567890123456789"));

        Assert.True(result.Succeeded, $"exit={result.ExitCode} timedOut={result.TimedOut}");
        // 5000 lines cannot have arrived intact: output is capped on purpose.
        Assert.Contains("truncated", result.StandardOutput);
    }

    [Fact]
    public async Task ReportsANonZeroExitCode()
    {

        var result = await new CommandRunner().RunAsync("cmd.exe", Cmd("exit 3"));

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task KillsAProcessThatOverrunsTheTimeout()
    {
        // ~30 s of pinging, given 1 s.
        var runner = new CommandRunner(TimeSpan.FromSeconds(1));

        var result = await runner.RunAsync("cmd.exe", Cmd("ping -n 30 127.0.0.1 > nul"));

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ReportsAMissingExecutableWithoutThrowing()
    {

        var result = await new CommandRunner().RunAsync("ultron-no-such-binary-xyz.exe", "/c whatever");

        Assert.False(result.Started);
        Assert.False(result.Succeeded);
        Assert.NotEqual(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task FallsBackToStderrWhenThereIsNoStdout()
    {
        var result = await new CommandRunner().RunAsync("cmd.exe", Cmd(">&2 echo problem"));

        Assert.Equal(string.Empty, result.StandardOutput.Trim());
        Assert.Contains("problem", result.Combined);
    }

    [Fact]
    public void DescribesAFailureAsAFailure()
    {
        // The old formatter emitted "Exit code: 1." as a normal-looking result,
        // so a refused mute or a failed shutdown read to the model as success.
        var failed = new CommandResult(1, "", "access denied", false, true);

        var text = failed.Describe("nircmd.exe");

        Assert.StartsWith("Failed:", text);
        Assert.Contains("access denied", text);
    }

    [Fact]
    public void DescribesATimeoutAsAFailure()
    {
        var text = new CommandResult(-1, "", "", true, true).Describe("snippingtool");

        Assert.StartsWith("Failed:", text);
        Assert.Contains("in time", text);
    }

    [Fact]
    public void DescribesSuccessWithTheExitCode()
    {
        var text = new CommandResult(0, "done", "", false, true).Describe("shutdown");

        Assert.StartsWith("Exit code: 0.", text);
        Assert.Contains("done", text);
    }

    [Fact]
    public void DescribesAnUnstartableProcessAsAFailure()
    {
        var text = new CommandResult(-1, "", "no such file", false, false).Describe("nope.exe");

        Assert.StartsWith("Failed:", text);
        Assert.Contains("nope.exe", text);
    }
}
