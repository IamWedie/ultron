using System.Linq;
using System.Text.Json;
using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// Phone tools are registered in the router, but the router asks the policy
/// before it runs a handler and denies unknown tools. A phone feature that is
/// wired end-to-end therefore still needs a policy entry for every curated name;
/// without one every phone call fails before reaching ADB. These tests pin that
/// invariant and the risk level of the destructive tools.
/// </summary>
public class PhonePolicyTests
{
    private static ToolCall Call(string name, string argsJson = "{}") =>
        new("id-1", name, JsonDocument.Parse(argsJson).RootElement.Clone());

    [Fact]
    public void EveryCuratedPhoneToolHasAPolicy()
    {
        var policy = new PolicyEngine();
        var missing = PhoneTools.Curated
            .Select(t => t.Name)
            .Where(n => policy.Evaluate(Call(n)).Outcome == PolicyOutcome.Deny)
            .OrderBy(n => n)
            .ToList();

        Assert.True(missing.Count == 0,
            "Phone tools with no policy (the router denies these): " + string.Join(", ", missing));
    }

    [Fact]
    public void ReadOnlyPhoneQueriesRunWithoutApproval()
    {
        var policy = new PolicyEngine();

        foreach (var name in new[] { "phone_device_info", "phone_battery", "phone_wifi_status" })
        {
            var decision = policy.Evaluate(Call(name));
            Assert.Equal(PolicyOutcome.Allow, decision.Outcome);
        }
    }

    [Theory]
    [InlineData("phone_make_call")]
    [InlineData("phone_send_sms")]
    [InlineData("phone_push_file")]
    [InlineData("phone_selfie_verify")]
    public void OutwardFacingPhoneToolsRequireHighRiskApproval(string name)
    {
        var decision = new PolicyEngine().Evaluate(Call(name));

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Equal(RiskLevel.High, decision.Risk);
    }

    [Fact]
    public void PhoneToolSummariesShowTheArgumentsTheUserIsApproving()
    {
        var policy = new PolicyEngine();

        var call = policy.Evaluate(Call("phone_send_sms",
            """{"number":"+15551234567","message":"on my way"}"""));

        Assert.Contains("+15551234567", call.Summary);
        Assert.Contains("on my way", call.Summary);
    }
}
