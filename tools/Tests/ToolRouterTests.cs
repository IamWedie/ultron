using System.Text.Json;
using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

/// <summary>Records what the router asked and answers with a scripted reply.</summary>
internal sealed class FakeApprovalService : IApprovalService
{
    public List<(string Tool, PolicyDecision Decision)> Requests { get; } = new();

    public bool Answer { get; set; } = true;

    public Exception? Throw { get; set; }

    public Task<bool> RequestApprovalAsync(ToolCall call, PolicyDecision decision, CancellationToken cancellationToken)
    {
        Requests.Add((call.Name, decision));
        if (Throw is not null) throw Throw;
        return Task.FromResult(Answer);
    }
}

internal sealed class StubHandler : IToolHandler
{
    private readonly Func<ToolCall, ToolResult> _run;

    public StubHandler(string name, Func<ToolCall, ToolResult>? run = null)
    {
        Name = name;
        _run = run ?? (_ => ToolResult.Ok($"{name} ran"));
    }

    public string Name { get; }

    public int Invocations { get; private set; }

    public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        Invocations++;
        return Task.FromResult(_run(call));
    }
}

public class ToolRouterTests
{
    private static ToolCall Call(string name, string argsJson = "{}")
    {
        using var doc = JsonDocument.Parse(argsJson);
        return new ToolCall("call-1", name, doc.RootElement);
    }

    private static ToolRouter Build(
        IEnumerable<IToolHandler> handlers,
        IApprovalService approvals,
        IActionPolicy? policy = null) =>
        new(handlers, policy ?? new PolicyEngine(), approvals);

    [Fact]
    public async Task UnknownTool_IsRefusedAndNeverExecuted()
    {
        var approvals = new FakeApprovalService();
        var router = Build(new IToolHandler[] { new StubHandler("known") }, approvals);

        var result = await router.ExecuteAsync(Call("mystery"));

        Assert.False(result.Success);
        Assert.Equal(ToolError.UnknownTool, result.Error);
        // Failing closed also means: no approval prompt for a tool nobody owns.
        Assert.Empty(approvals.Requests);
    }

    [Fact]
    public async Task LowRiskTool_RunsWithoutAsking()
    {
        var approvals = new FakeApprovalService();
        var handler = new StubHandler("recall_memory");
        var router = Build(new IToolHandler[] { handler }, approvals);

        var result = await router.ExecuteAsync(Call("recall_memory", """{"query":"my name"}"""));

        Assert.True(result.Success);
        Assert.Equal(1, handler.Invocations);
        Assert.Empty(approvals.Requests);
    }

    [Fact]
    public async Task HighRiskTool_AsksFirst()
    {
        var approvals = new FakeApprovalService { Answer = true };
        var handler = new StubHandler("type_text");
        var router = Build(new IToolHandler[] { handler }, approvals);

        var result = await router.ExecuteAsync(Call("type_text", """{"text":"hello"}"""));

        Assert.True(result.Success);
        Assert.Equal(1, handler.Invocations);
        var request = Assert.Single(approvals.Requests);
        Assert.Equal("type_text", request.Tool);
        Assert.Equal(RiskLevel.Medium, request.Decision.Risk);
        // The prompt must show the user what is about to happen.
        Assert.Contains("hello", request.Decision.Summary);
    }

    [Fact]
    public async Task DeniedApproval_DoesNotExecuteTheHandler()
    {
        var approvals = new FakeApprovalService { Answer = false };
        var handler = new StubHandler("type_text");
        var router = Build(new IToolHandler[] { handler }, approvals);

        var result = await router.ExecuteAsync(Call("type_text", """{"text":"hello"}"""));

        Assert.False(result.Success);
        Assert.Equal(ToolError.Denied, result.Error);
        Assert.Equal(0, handler.Invocations);
    }

    [Fact]
    public async Task ToolWithNoPolicy_IsDeniedEvenIfAHandlerExists()
    {
        // The dangerous case: a handler added without a policy entry.
        var approvals = new FakeApprovalService { Answer = true };
        var handler = new StubHandler("brand_new_tool");
        var router = Build(new IToolHandler[] { handler }, approvals);

        var result = await router.ExecuteAsync(Call("brand_new_tool"));

        Assert.False(result.Success);
        Assert.Equal(ToolError.Denied, result.Error);
        Assert.Equal(0, handler.Invocations);
        Assert.Empty(approvals.Requests);
    }

    [Fact]
    public async Task PolicyThatThrows_RefusesToRun()
    {
        var approvals = new FakeApprovalService { Answer = true };
        var handler = new StubHandler("type_text");
        var router = Build(new IToolHandler[] { handler }, approvals, new ThrowingPolicy());

        var result = await router.ExecuteAsync(Call("type_text", """{"text":"x"}"""));

        Assert.False(result.Success);
        Assert.Equal(0, handler.Invocations);
    }

    [Fact]
    public async Task ApprovalServiceThatThrows_RefusesToRun()
    {
        var approvals = new FakeApprovalService { Throw = new InvalidOperationException("no UI thread") };
        var handler = new StubHandler("type_text");
        var router = Build(new IToolHandler[] { handler }, approvals);

        var result = await router.ExecuteAsync(Call("type_text", """{"text":"x"}"""));

        Assert.False(result.Success);
        Assert.Equal(ToolError.Denied, result.Error);
        Assert.Equal(0, handler.Invocations);
    }

    [Fact]
    public async Task HandlerException_BecomesAFailedResult()
    {
        var approvals = new FakeApprovalService();
        var router = Build(
            new IToolHandler[] { new StubHandler("system_status", _ => throw new IOException("disk gone")) },
            approvals);

        var result = await router.ExecuteAsync(Call("system_status"));

        Assert.False(result.Success);
        Assert.Equal(ToolError.Failed, result.Error);
        Assert.Contains("disk gone", result.Message);
    }

    [Fact]
    public async Task BadArguments_BecomeInvalidArguments()
    {
        var approvals = new FakeApprovalService();
        var router = Build(
            new IToolHandler[]
            {
                new StubHandler("recall_memory", call => call.Deserialize<RecallArgs>().Query.Length > 0
                    ? ToolResult.Ok("found")
                    : ToolResult.Ok("none")),
            },
            approvals);

        // "query" is an object, so the typed deserialize throws.
        var result = await router.ExecuteAsync(Call("recall_memory", """{"query":{"bad":true}}"""));

        Assert.False(result.Success);
        Assert.Equal(ToolError.InvalidArguments, result.Error);
    }

    [Fact]
    public async Task Cancellation_BeforeExecution_RunsNothing()
    {
        var approvals = new FakeApprovalService();
        var handler = new StubHandler("recall_memory");
        var router = Build(new IToolHandler[] { handler }, approvals);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await router.ExecuteAsync(Call("recall_memory"), cts.Token);

        Assert.Equal(ToolError.Cancelled, result.Error);
        Assert.Equal(0, handler.Invocations);
    }

    [Fact]
    public async Task HandlerReturningNull_IsStillAWellFormedResult()
    {
        var approvals = new FakeApprovalService();
        var router = Build(
            new IToolHandler[] { new StubHandler("system_status", _ => null!) },
            approvals);

        var result = await router.ExecuteAsync(Call("system_status"));

        Assert.False(result.Success);
        Assert.Equal(ToolError.Failed, result.Error);
    }

    [Fact]
    public void DuplicateHandlerNames_AreRejectedAtConstruction()
    {
        var handlers = new IToolHandler[] { new StubHandler("dup"), new StubHandler("DUP") };

        Assert.Throws<ArgumentException>(() =>
            new ToolRouter(handlers, new PolicyEngine(), new FakeApprovalService()));
    }

    [Fact]
    public async Task ToolNamesAreMatchedCaseInsensitively()
    {
        var approvals = new FakeApprovalService();
        var handler = new StubHandler("Recall_Memory");
        var router = Build(new IToolHandler[] { handler }, approvals);

        var result = await router.ExecuteAsync(Call("recall_memory"));

        Assert.True(result.Success);
        Assert.Equal(1, handler.Invocations);
    }

    [Fact]
    public async Task DelegateToolHandlerAdaptsSyncAndAsyncFunctions()
    {
        var approvals = new FakeApprovalService();
        var sync = new DelegateToolHandler("youtube_video", (call, _) => ToolResult.Ok("sync"));
        var async = new DelegateToolHandler("reminder", (call, ct) =>
            Task.FromResult(ToolResult.Ok("async:" + call.GetString("message"))));
        var router = Build(new IToolHandler[] { sync, async }, approvals);

        Assert.True((await router.ExecuteAsync(Call("youtube_video"))).Success);
        var reminder = await router.ExecuteAsync(Call("reminder", """{"message":"stand up"}"""));
        Assert.Equal("async:stand up", reminder.Message);
    }

    private sealed record RecallArgs(string Query);

    private sealed class ThrowingPolicy : IActionPolicy
    {
        public PolicyDecision Evaluate(ToolCall call) => throw new InvalidOperationException("policy table unavailable");
    }
}
