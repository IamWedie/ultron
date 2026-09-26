using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// Every tool outcome - success, refusal, bad arguments, crash - must arrive in
/// one shape so the router, the audit log and the model never special-case a
/// bare string.
/// </summary>
public class ToolResultTests
{
    [Fact]
    public void OkIsSuccessfulAndReadsAsItsMessage()
    {
        var result = ToolResult.Ok("wrote 12 chars");

        Assert.True(result.Success);
        Assert.Equal(ToolError.None, result.Error);
        Assert.Equal("wrote 12 chars", result.ToModelString());
    }

    [Fact]
    public void OkCanCarryStructuredData()
    {
        var result = ToolResult.Ok("done", new { path = "C:/tmp/a.txt", bytes = 12 });

        Assert.NotNull(result.Data);
        Assert.Equal("C:/tmp/a.txt", result.Data!.Value.GetProperty("path").GetString());
        Assert.Equal(12, result.Data.Value.GetProperty("bytes").GetInt32());
    }

    [Fact]
    public void FailureIsTaggedSoTheModelCanTellRefusalFromNoResult()
    {
        var result = ToolResult.Denied("User denied approval for delete_file.");

        Assert.False(result.Success);
        Assert.Equal(ToolError.Denied, result.Error);
        Assert.Equal("ERROR [Denied]: User denied approval for delete_file.", result.ToModelString());
    }

    [Theory]
    [InlineData(ToolError.UnknownTool)]
    [InlineData(ToolError.InvalidArguments)]
    [InlineData(ToolError.NotFound)]
    [InlineData(ToolError.Unsupported)]
    [InlineData(ToolError.TimedOut)]
    [InlineData(ToolError.Cancelled)]
    [InlineData(ToolError.Failed)]
    public void EveryFailureCodeProducesAStableTag(ToolError error)
    {
        var result = ToolResult.Fail("something went wrong", error);

        Assert.Equal($"ERROR [{error}]: something went wrong", result.ToModelString());
    }

    [Fact]
    public void FactoriesSetTheMatchingErrorCode()
    {
        Assert.Equal(ToolError.InvalidArguments, ToolResult.InvalidArguments("x").Error);
        Assert.Equal(ToolError.NotFound, ToolResult.NotFound("x").Error);
        Assert.Equal(ToolError.Unsupported, ToolResult.Unsupported("x").Error);
        Assert.Equal(ToolError.Cancelled, ToolResult.Cancelled("x").Error);
        Assert.Equal(ToolError.Failed, ToolResult.Fail("x").Error);
    }

    [Fact]
    public void UnserializableDataDoesNotFailTheCallItWasDescribing()
    {
        // A diagnostic payload must never be the reason a tool result is lost.
        var result = ToolResult.Ok("done", new SelfReferencing());

        Assert.True(result.Success);
        Assert.Null(result.Data);
    }

    private sealed class SelfReferencing
    {
        public SelfReferencing Self => this;
    }
}
