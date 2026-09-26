using System.Text.Json;
using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// Tool arguments arrive from the backend process as untrusted JSON. The old
/// Dictionary&lt;string, object&gt; flattened every value to text, which erased
/// types and turned a JSON null into the literal string "null".
/// </summary>
public class ToolCallTests
{
    private static ToolCall ParseCall(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new ToolCall("call-1", "test_tool", doc.RootElement.GetProperty("args"));
    }

    [Fact]
    public void PreservesNumberType_InsteadOfFlatteningToText()
    {
        var call = ParseCall("""{"args":{"amount":3,"x":10}}""");

        Assert.Equal(3, call.GetInt32("amount"));
        Assert.Equal(10, call.GetInt32("x"));
    }

    [Fact]
    public void PreservesBooleanType_AndDoesNotStringifyNull()
    {
        var call = ParseCall("""{"args":{"ok":true,"nothing":null}}""");

        Assert.True(call.GetBoolean("ok"));
        // A JSON null must not read back as the string "null".
        Assert.Null(call.GetString("nothing"));
        Assert.False(call.Has("nothing"));
    }

    [Fact]
    public void ArgumentsSurviveDisposalOfTheSourceDocument()
    {
        ToolCall call;
        using (var doc = JsonDocument.Parse("""{"args":{"text":"hello","repeat":2}}"""))
        {
            call = new ToolCall("call-1", "test_tool", doc.RootElement.GetProperty("args"));
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // The element was detached with Clone(); reading it must not throw and
        // must not return garbage.
        Assert.Equal("hello", call.GetString("text"));
        Assert.Equal(2, call.GetInt32("repeat"));
    }

    [Theory]
    [InlineData("""{"args":null}""")]
    [InlineData("""{"args":{}}""")]
    [InlineData("""{}""")]
    public void MissingOrNullArgumentsBecomeAnEmptyObject(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var args = doc.RootElement.TryGetProperty("args", out var el) ? el : default;

        var call = new ToolCall("call-1", "test_tool", args);

        Assert.Equal(JsonValueKind.Object, call.Arguments.ValueKind);
        Assert.Empty(call.Arguments.EnumerateObject());
    }

    [Theory]
    [InlineData("""{"args":[1,2,3]}""")]
    [InlineData("""{"args":"oops"}""")]
    [InlineData("""{"args":7}""")]
    public void RejectsNonObjectArguments(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var args = doc.RootElement.GetProperty("args");

        Assert.Throws<ArgumentException>(() => new ToolCall("call-1", "test_tool", args));
    }

    [Theory]
    [InlineData("", "tool")]
    [InlineData("   ", "tool")]
    [InlineData("id", "")]
    [InlineData("id", "  ")]
    public void RejectsMissingIdOrName(string id, string name)
    {
        using var doc = JsonDocument.Parse("{}");

        Assert.Throws<ArgumentException>(() => new ToolCall(id, name, doc.RootElement));
    }

    [Fact]
    public void DeserializesTypedArguments()
    {
        var call = ParseCall("""{"args":{"text":"hello","repeat":3}}""");

        var args = call.Deserialize<TypeTextArgs>();

        Assert.Equal("hello", args.Text);
        Assert.Equal(3, args.Repeat);
    }

    [Fact]
    public void DeserializeThrowsToolArgumentExceptionOnShapeMismatch()
    {
        // "text" is an object, not a string: the router turns this one exception
        // into a single InvalidArguments result.
        var call = ParseCall("""{"args":{"text":{"nested":true}}}""");

        Assert.Throws<ToolArgumentException>(() => call.Deserialize<TypeTextArgs>());
    }

    [Fact]
    public void GetStringReturnsNullForNonScalarValues()
    {
        var call = ParseCall("""{"args":{"obj":{"a":1},"arr":[1],"s":"ok"}}""");

        Assert.Equal("ok", call.GetString("s"));
        Assert.Null(call.GetString("obj"));
        Assert.Null(call.GetString("arr"));
        Assert.Null(call.GetString("missing"));
    }

    [Fact]
    public void GetInt32AcceptsQuotedNumbersButRejectsJunk()
    {
        var call = ParseCall("""{"args":{"quoted":"42","bad":"abc","obj":{}}}""");

        Assert.Equal(42, call.GetInt32("quoted"));
        Assert.Null(call.GetInt32("bad"));
        Assert.Null(call.GetInt32("obj"));
    }

    [Fact]
    public void GetBooleanAcceptsStringsAndRejectsOthers()
    {
        var call = ParseCall("""{"args":{"yes":true,"quoted":"false","n":0}}""");

        Assert.True(call.GetBoolean("yes"));
        Assert.False(call.GetBoolean("quoted"));
        Assert.Null(call.GetBoolean("n"));
    }

    [Fact]
    public void DoesNotMatchJsonNullAsTheStringNull()
    {
        // Regression guard: JsonElement.ToString() on a null yields "null", which
        // is why the legacy flattening was unsafe for path arguments.
        var call = ParseCall("""{"args":{"file_path":null}}""");

        Assert.Null(call.GetString("file_path"));
    }

    [Fact]
    public void DeserializeMapsSnakeCaseWireNamesToPascalCaseProperties()
    {
        // Real payloads use snake_case; handlers are written in PascalCase.
        var call = ParseCall("""{"args":{"app_name":"notepad","file_path":"C:/a.txt","message_text":"hi"}}""");

        var args = call.Deserialize<OpenAppArgs>();

        Assert.Equal("notepad", args.AppName);
    }

    [Fact]
    public void DeserializeAcceptsMissingOptionalProperties()
    {
        var call = ParseCall("""{"args":{"app_name":"notepad"}}""");

        var args = call.Deserialize<OpenAppArgs>();

        Assert.Equal("notepad", args.AppName);
        Assert.Null(args.WindowTitle);
    }

    private sealed record TypeTextArgs(string Text, int Repeat = 1);

    private sealed record OpenAppArgs(string AppName, string? WindowTitle = null);
}
