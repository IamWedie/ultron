using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// A tool result is delivered to the backend over stdin and forwarded to Gemini
/// by the Python side. Nothing ever acknowledges it back, so the C# side must
/// not wait for an acknowledgement that cannot arrive.
/// </summary>
public class ToolResultLifecycleTests
{
    [Fact]
    public async Task SendToolResultAsync_DoesNotWaitForAnAckThatNeverArrives()
    {
        using var backend = new GeminiBackend();

        var started = DateTime.UtcNow;
        var send = backend.SendToolResultAsync("call-1", "test_tool", ToolResult.Ok("done"));
        var finished = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.True(
            ReferenceEquals(send, finished),
            "SendToolResultAsync must complete on write, not block until a timeout.");
        Assert.True(
            DateTime.UtcNow - started < TimeSpan.FromSeconds(5),
            "Every tool call used to stall for the full tool timeout.");
    }

    [Fact]
    public async Task SendToolResultAsync_SendsEveryResultWithoutBlockingTheNext()
    {
        using var backend = new GeminiBackend();

        var first = backend.SendToolResultAsync("call-1", "test_tool", ToolResult.Ok("one"));
        var second = backend.SendToolResultAsync("call-2", "test_tool", ToolResult.Ok("two"));
        var both = Task.WhenAll(first, second);

        var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.True(
            ReferenceEquals(both, finished),
            "consecutive tool results must not queue behind each other.");
    }
}
