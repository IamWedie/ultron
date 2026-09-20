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

    [Fact]
    public void ProcessLine_Contact_RaisesEvent()
    {
        using var be = new GeminiBackend();
        ContactRequest? req = null;
        be.ContactRequested += r => req = r;
        be.ProcessLine("""{"type":"contact","id":"5","area":"guardian","text":"CPU at 94%","priority":"high","ask":false,"reply_id":""}""");
        Assert.NotNull(req);
        Assert.Equal("guardian", req!.Area);
        Assert.Equal("CPU at 94%", req.Text);
        Assert.Equal("high", req.Priority);
        Assert.False(req.Ask);
    }

    [Fact]
    public void ProcessLine_Contact_MissingFields_Defaults()
    {
        using var be = new GeminiBackend();
        ContactRequest? req = null;
        be.ContactRequested += r => req = r;
        be.ProcessLine("""{"type":"contact","text":"ping"}""");
        Assert.NotNull(req);
        Assert.Equal("guardian", req!.Area);
        Assert.Equal("normal", req.Priority);
        Assert.False(req.Ask);
    }

    [Fact]
    public void ProcessLine_TelegramStatus_RaisesEvent()
    {
        using var be = new GeminiBackend();
        (string Phase, string Message, bool Available)? got = null;
        be.TelegramStatusChanged += (p, m, a) => got = (p, m, a);
        be.ProcessLine("""{"type":"telegram_status","phase":"connected","message":"CONNECTED","available":true}""");
        Assert.Equal(("connected", "CONNECTED", true), got);
    }

    [Fact]
    public void ProcessLine_TelegramCodeRequired_RaisesEvent()
    {
        using var be = new GeminiBackend();
        (string Phone, string Hint)? got = null;
        be.TelegramCodeRequired += (p, h) => got = (p, h);
        be.ProcessLine("""{"type":"telegram_code_required","phone":"+15551234","hint":"Enter code"}""");
        Assert.Equal(("+15551234", "Enter code"), got);
    }

    [Fact]
    public void ProcessLine_TelegramCodeResult_RaisesEvent()
    {
        using var be = new GeminiBackend();
        (bool Ok, string Msg)? got = null;
        be.TelegramCodeResult += (o, m) => got = (o, m);
        be.ProcessLine("""{"type":"telegram_code_result","ok":true,"message":"Logged in"}""");
        Assert.Equal((true, "Logged in"), got);
    }

    [Fact]
    public void ProcessLine_TelegramTargetResult_RaisesEvent()
    {
        using var be = new GeminiBackend();
        TelegramTargetResult? got = null;
        be.TelegramTargetResolved += r => got = r;
        be.ProcessLine("""{"type":"telegram_target_result","ok":true,"target":"@owner","user_id":"12345","name":"Owner","message":"Target resolved: @owner [12345]"}""");
        Assert.NotNull(got);
        Assert.True(got!.Ok);
        Assert.Equal("@owner", got.Target);
        Assert.Equal("12345", got.UserId);
        Assert.Equal("Owner", got.Name);
    }

    [Theory]
    [InlineData("""{"type":"telegram_status"}""")]
    [InlineData("""{"type":"telegram_code_required"}""")]
    [InlineData("""{"type":"telegram_code_result"}""")]
    [InlineData("""{"type":"telegram_target_result"}""")]
    public void ProcessLine_Telegram_MissingFields_DoesNotThrow(string line)
    {
        using var be = new GeminiBackend();
        var ex = Record.Exception(() => be.ProcessLine(line));
        Assert.Null(ex);
    }
}