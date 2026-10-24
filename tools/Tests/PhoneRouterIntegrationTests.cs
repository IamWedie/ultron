using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ultron.Services;
using Ultron.Services.Tools.Phone;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// The phone bridge tests exercised <see cref="PhoneToolService"/> directly, which
/// bypasses the router and the policy - the exact layer that denied every phone
/// call. This test drives the real router with a fake device so a regression in
/// policy, registration or dispatch fails here.
/// </summary>
public class PhoneRouterIntegrationTests
{
    private sealed class FakePhone : IAdbPhone
    {
        public List<string> Calls { get; } = [];

        public ToolResult Execute(string name, JsonNode? args)
        {
            Calls.Add(name);
            return ToolResult.Ok($"{name} ran.");
        }
    }

    private static ToolCall Call(string name, string argsJson = "{}") =>
        new("call-1", name, JsonDocument.Parse(argsJson).RootElement.Clone());

    private static ToolRouter Build(FakePhone phone, FakeApprovalService approvals) =>
        new(new PhoneToolService(phone).Handlers, new PolicyEngine(), approvals);

    [Fact]
    public async Task AReadOnlyPhoneToolReachesTheDeviceWithoutApproval()
    {
        var phone = new FakePhone();
        var approvals = new FakeApprovalService();
        var router = Build(phone, approvals);

        var result = await router.ExecuteAsync(Call("phone_device_info"));

        Assert.True(result.Success, result.Message);
        Assert.Equal(["phone_device_info"], phone.Calls);
        Assert.Empty(approvals.Requests);
    }

    [Fact]
    public async Task AnApprovedCallRunsAndAnUnapprovedCallDoesNot()
    {
        var phone = new FakePhone();
        var approvals = new FakeApprovalService { Answer = true };
        var router = Build(phone, approvals);

        var approved = await router.ExecuteAsync(Call("phone_make_call", """{"number":"+15551234567"}"""));
        Assert.True(approved.Success, approved.Message);
        Assert.Single(approvals.Requests);
        Assert.Equal(RiskLevel.High, approvals.Requests[0].Decision.Risk);

        approvals.Answer = false;
        var denied = await router.ExecuteAsync(Call("phone_make_call", """{"number":"+15551234567"}"""));

        Assert.False(denied.Success);
        Assert.Equal(ToolError.Denied, denied.Error);
        Assert.Single(phone.Calls);
    }

    [Fact]
    public async Task ANonCuratedPhoneToolIsRefusedByTheRouter()
    {
        var phone = new FakePhone();
        var router = Build(phone, new FakeApprovalService());

        var result = await router.ExecuteAsync(Call("phone_system_info"));

        Assert.False(result.Success);
        Assert.Equal(ToolError.UnknownTool, result.Error);
        Assert.Empty(phone.Calls);
    }
}
