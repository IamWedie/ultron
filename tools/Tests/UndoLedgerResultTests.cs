using System;
using Ultron.Services;
using Ultron.Services.Processes;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// The undo tool used to hand the model a sentence and wrap it in Ok, so an
/// empty ledger ("Nothing to undo.") and a reversal that had thrown ("Undo of X
/// failed: ...") both reached the user as "undone". These lock the
/// classification in.
/// </summary>
public class UndoLedgerResultTests
{
    [Fact]
    public void AnEmptyLedgerIsAMissRatherThanASuccess()
    {
        var result = new UndoLedger().UndoResult();

        Assert.False(result.Success);
        Assert.Equal(ToolError.NotFound, result.Error);
        Assert.Equal("Nothing to undo.", result.Message);
    }

    [Fact]
    public void AReversalIsASuccess()
    {
        var undo = new UndoLedger();
        undo.Push("write two.txt", () => "Restored two.txt.");

        var result = undo.UndoResult();

        Assert.True(result.Success);
        Assert.Equal(ToolError.None, result.Error);
        Assert.Contains("Undid write two.txt.", result.Message);
    }

    [Fact]
    public void AReversalThatThrowsIsAFailureAndStillReportsWhy()
    {
        var undo = new UndoLedger();
        undo.Push("delete a.txt", () => throw new InvalidOperationException("file is locked"));

        var result = undo.UndoResult();

        Assert.False(result.Success);
        Assert.Equal(ToolError.Failed, result.Error);
        Assert.Contains("Undo of delete a.txt failed", result.Message);
        Assert.Contains("file is locked", result.Message);
    }

    [Fact]
    public void AFailedReversalIsStillConsumedSoItIsNotRetriedForever()
    {
        var undo = new UndoLedger();
        undo.Push("delete a.txt", () => throw new InvalidOperationException("nope"));

        Assert.False(undo.UndoResult().Success);
        Assert.Equal(0, undo.Count);
        Assert.Equal(ToolError.NotFound, undo.UndoResult().Error);
    }

    [Fact]
    public void UndoStillReturnsTheSameSentenceForTheTranscript()
    {
        var undo = new UndoLedger();
        Assert.Equal("Nothing to undo.", undo.Undo());

        undo.Push("write a.txt", () => "Restored a.txt.");
        Assert.Equal("Undid write a.txt. Restored a.txt.", undo.Undo());
    }
}

/// <summary>
/// CommandResult.Describe rewrites a failed process as a sentence beginning
/// "Failed:", but keeps no success flag, so every caller wrapped it in Ok. A
/// refused mute or a failed shutdown was reported to the user as done.
/// </summary>
public class CommandResultMappingTests
{
    [Fact]
    public void AZeroExitIsASuccess()
    {
        var result = new CommandResult(0, "ok", "", TimedOut: false, Started: true).ToResult("nircmd.exe");

        Assert.True(result.Success);
        Assert.Equal(ToolError.None, result.Error);
        Assert.Contains("Exit code: 0", result.Message);
    }

    [Fact]
    public void ANonZeroExitIsAFailure()
    {
        var result = new CommandResult(1, "", "access denied", TimedOut: false, Started: true)
            .ToResult("nircmd.exe");

        Assert.False(result.Success);
        Assert.Equal(ToolError.Failed, result.Error);
        Assert.Contains("Failed", result.Message);
        Assert.Contains("access denied", result.Message);
    }

    [Fact]
    public void ATimeoutIsAFailureNotASilentHang()
    {
        var result = new CommandResult(0, "", "", TimedOut: true, Started: true).ToResult("ping");

        Assert.False(result.Success);
        Assert.Equal(ToolError.Failed, result.Error);
        Assert.Contains("did not finish in time", result.Message);
    }

    [Fact]
    public void AProcessThatNeverStartedIsAFailure()
    {
        var result = new CommandResult(-1, "", "not found", TimedOut: false, Started: false)
            .ToResult("ghost.exe");

        Assert.False(result.Success);
        Assert.Equal(ToolError.Failed, result.Error);
        Assert.Contains("could not start", result.Message);
    }

    [Fact]
    public void ToResultAgreesWithSucceeded()
    {
        var samples = new[]
        {
            new CommandResult(0, "a", "", false, true),
            new CommandResult(1, "", "b", false, true),
            new CommandResult(0, "", "", true, true),
            new CommandResult(0, "", "c", false, false),
        };

        foreach (var sample in samples)
        {
            Assert.Equal(sample.Succeeded, sample.ToResult("cmd").Success);
        }
    }
}
