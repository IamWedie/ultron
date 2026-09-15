using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

public class AppLogTests
{
    [Theory]
    [InlineData("AIzaSyA1234567890abcdefgh_ijklmnopqrstuv", "***REDACTED***")]
    [InlineData("AQ.MhzC7ykjyqEXAMPLEKEY___longtoken12345", "***REDACTED***")]
    [InlineData("sk-beeeef0ef0ef0ef0ef0ef0ef0ef0ef0", "***REDACTED***")]
    [InlineData("token abc123", "token abc123")]
    public void Redact_StripsSecrets(string input, string expected)
    {
        var redacted = AppLog.Redact(input);
        if (expected == "***REDACTED***")
            Assert.Contains("***REDACTED***", redacted);
        else
            Assert.DoesNotContain("***REDACTED***", redacted);
    }

    [Fact]
    public void Redact_PreservesNormalText()
    {
        Assert.Equal("Status: connected — hello world", AppLog.Redact("Status: connected — hello world"));
    }

    [Theory]
    [InlineData("Error", "Error")]
    [InlineData("debug", "Debug")]
    [InlineData("garbage", "Info")] // unknown falls back without crashing
    public void SetVerbosity_ParsesLevels(string name, string expect)
    {
        AppLog.SetVerbosity(name);
        Assert.Equal(Enum.Parse<AppLog.Level>(expect, true), AppLog.Verbosity);
        AppLog.SetVerbosity("Info");
    }

    [Fact]
    public void Write_NonSecretLine_NoThrow()
    {
        var ex = Record.Exception(() => AppLog.Write("Test", "some regular line"));
        Assert.Null(ex);
    }
}