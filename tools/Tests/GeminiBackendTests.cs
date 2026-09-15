using System.Text.Json;
using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

public class GeminiBackendTests
{
    [Theory]
    [InlineData("""{"type":"status","state":"connected","message":"hi"}""", "connected")]
    [InlineData("""{"type":"transcript","role":"assistant","text":"hello"}""", null)]
    public void ProcessLine_Status_SetsState(string line, string? expectState)
    {
        using var be = new GeminiBackend();
        be.ProcessLine(line);
        if (expectState is not null)
            Assert.Equal(expectState, be.State);
    }

    [Fact]
    public void ProcessLine_ToolCall_RaisesEvent()
    {
        using var be = new GeminiBackend();
        GeminiToolCall? call = null;
        be.ToolCallReceived += c => call = c;
        be.ProcessLine("""{"type":"tool_call","id":"t1","name":"press_key","args":{"keys":"ctrl+s"}}""");
        Assert.NotNull(call);
        Assert.Equal("t1", call.Id);
        Assert.Equal("press_key", call.Name);
    }

    [Fact]
    public void ProcessLine_ComputerAct_RaisesEventWithAction()
    {
        using var be = new GeminiBackend();
        (string Id, JsonElement Action)? got = null;
        be.ComputerActRequested += (id, action) => got = (id, action);
        be.ProcessLine("""{"type":"computer_act","id":"9","action":{"type":"click","x":5,"y":7}}""");
        Assert.NotNull(got);
        Assert.Equal("9", got.Value.Id);
        Assert.Equal("click", got.Value.Action.GetProperty("type").GetString());
    }

    [Fact]
    public void ProcessLine_Hello_SetsVersionAndCapabilities()
    {
        using var be = new GeminiBackend();
        be.ProcessLine("""{"type":"hello","version":"2.0","capabilities":["voice","guardian"]}""");
        Assert.Equal("2.0", be.BackendVersion);
        Assert.Contains("guardian", be.BackendCapabilities);
    }

    [Fact]
    public void ProcessLine_Error_RaisesErrorEvent()
    {
        using var be = new GeminiBackend();
        string? err = null;
        be.ErrorOccurred += m => err = m;
        be.ProcessLine("""{"type":"error","message":"boom"}""");
        Assert.Equal("boom", err);
    }

    [Fact]
    public void ProcessLine_Malformed_DoesNotThrow()
    {
        using var be = new GeminiBackend();
        var ex = Record.Exception(() => be.ProcessLine("not json at all"));
        Assert.Null(ex);
        var ex2 = Record.Exception(() => be.ProcessLine("""{"type":"transcript"}"""));
        Assert.Null(ex2);
    }

    [Fact]
    public void ProcessLine_UnknownType_Ignored()
    {
        using var be = new GeminiBackend();
        var ex = Record.Exception(() => be.ProcessLine("""{"type":"totally_unknown","x":1}"""));
        Assert.Null(ex);
    }
}