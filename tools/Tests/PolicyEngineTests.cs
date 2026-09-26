using System.Text.Json;
using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// The risk table is the app's security policy. These tests pin the decisions
/// that must not silently change.
/// </summary>
public class PolicyEngineTests
{
    private static ToolCall Call(string name, string argsJson = "{}")
    {
        using var doc = JsonDocument.Parse(argsJson);
        return new ToolCall("call-1", name, doc.RootElement);
    }

    private readonly PolicyEngine _policy = new();

    [Theory]
    // Read-only: no prompt.
    [InlineData("recall_memory", """{"query":"x"}""", RiskLevel.Read)]
    [InlineData("system_status", "{}", RiskLevel.Read)]
    // Everyday local actions: no prompt.
    [InlineData("web_search", """{"query":"weather"}""", RiskLevel.Low)]
    [InlineData("reminder", """{"message":"x"}""", RiskLevel.Low)]
    // Anything that acts on the machine: prompt.
    [InlineData("type_text", """{"text":"x"}""", RiskLevel.Medium)]
    [InlineData("press_key", """{"keys":"ctrl+s"}""", RiskLevel.Medium)]
    [InlineData("open_app", """{"app_name":"notepad"}""", RiskLevel.Medium)]
    [InlineData("computer_settings", """{"action":"volume"}""", RiskLevel.Medium)]
    // Outward-facing or destructive: prompt.
    [InlineData("send_message", """{"receiver":"+15551234","message_text":"hi"}""", RiskLevel.High)]
    [InlineData("shutdown_jarvis", "{}", RiskLevel.Critical)]
    [InlineData("computer_use", """{"task":"do things"}""", RiskLevel.Critical)]
    public void RiskLevelsArePinned(string tool, string args, RiskLevel expected)
    {
        var decision = _policy.Evaluate(Call(tool, args));

        Assert.Equal(expected, decision.Risk);
        Assert.Equal(
            expected <= RiskLevel.Low ? PolicyOutcome.Allow : PolicyOutcome.RequireApproval,
            decision.Outcome);
    }

    [Theory]
    [InlineData("read", RiskLevel.Read)]
    [InlineData("list", RiskLevel.Read)]
    [InlineData("browse", RiskLevel.Read)]
    [InlineData("search", RiskLevel.Read)]
    [InlineData("write", RiskLevel.Medium)]
    [InlineData("create", RiskLevel.Medium)]
    [InlineData("rename", RiskLevel.Medium)]
    [InlineData("move", RiskLevel.Medium)]
    [InlineData("copy", RiskLevel.Medium)]
    [InlineData("delete", RiskLevel.High)]
    public void FileRiskDependsOnTheAction(string action, RiskLevel expected)
    {
        var decision = _policy.Evaluate(Call("file_processor", $$"""{"action":"{{action}}","file_path":"C:/a.txt"}"""));

        Assert.Equal(expected, decision.Risk);
    }

    [Fact]
    public void UnrecognisedFileAction_IsTreatedAsHighRisk()
    {
        // A typo or an invented verb must not become a free pass.
        var decision = _policy.Evaluate(Call("file_processor", """{"action":"exfiltrate"}"""));

        Assert.Equal(RiskLevel.High, decision.Risk);
        Assert.True(decision.NeedsApproval);
    }

    [Fact]
    public void FileProcessorWithoutAnAction_IsTreatedAsHighRisk()
    {
        var decision = _policy.Evaluate(Call("file_processor"));

        Assert.Equal(RiskLevel.High, decision.Risk);
    }

    [Fact]
    public void UnknownTool_IsDenied()
    {
        var decision = _policy.Evaluate(Call("totally_unknown"));

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
    }

    [Fact]
    public void SummaryShowsTheUserTheAction()
    {
        var decision = _policy.Evaluate(Call("type_text", """{"text":"rm -rf /"}"""));

        Assert.Contains("rm -rf /", decision.Summary);
    }

    [Fact]
    public void SummaryMarksMissingArgumentsInsteadOfPrintingNothing()
    {
        var decision = _policy.Evaluate(Call("open_app"));

        Assert.Contains("(none)", decision.Summary);
    }

    [Fact]
    public void SummaryTruncatesHugeArguments()
    {
        // A model-supplied megabyte must not be pasted into a dialog.
        var huge = new string('x', 50_000);
        var call = Call("type_text", JsonSerializer.Serialize(new { text = huge }));

        var decision = _policy.Evaluate(call);

        Assert.True(decision.Summary.Length < 500, $"summary was {decision.Summary.Length} chars");
        Assert.Contains("…", decision.Summary);
    }

    [Fact]
    public void DeleteSummaryNamesTheTargetPath()
    {
        var decision = _policy.Evaluate(Call("file_processor", """{"action":"delete","file_path":"C:/Users/me/notes.txt"}"""));

        Assert.Equal(RiskLevel.High, decision.Risk);
        Assert.Contains("notes.txt", decision.Summary);
    }

    [Fact]
    public void SummaryTreatsJsonNullAsMissingRatherThanTheStringNull()
    {
        var decision = _policy.Evaluate(Call("type_text", """{"text":null}"""));

        Assert.Contains("(none)", decision.Summary);
        Assert.DoesNotContain(": null", decision.Summary);
    }
}
