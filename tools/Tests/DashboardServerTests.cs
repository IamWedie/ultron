using System.Net;
using System.Net.Sockets;
using System.Text;
using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

public sealed class DashboardServerTests
{
    [Fact]
    public async Task State_Endpoint_Requires_Token_And_Returns_Snapshot()
    {
        using var server = new DashboardServer(0);
        Assert.True(server.Start());
        try
        {
            var port = server.ActualPort;
            Assert.True(port > 0);
            using var hc = new HttpClient();

            var denied = await hc.GetAsync($"http://127.0.0.1:{port}/api/state");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

            server.PushTranscript("user", "hello world \"quoted\"");
            server.PushStatus("awake", true);
            server.PushConfidence(0.9f);
            server.PushMute(true);

            var ok = await hc.GetStringAsync($"http://127.0.0.1:{port}/api/state?k={server.Token}");
            Assert.Contains("\"type\":\"snapshot\"", ok);
            Assert.Contains("hello world", ok);
            Assert.Contains("\"status\":\"awake\"", ok);
            Assert.Contains("\"confidence\":0.9", ok);
            Assert.Contains("\"muted\":true", ok);
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task Sse_Pushes_Live_Events_To_Authenticated_Client()
    {
        using var server = new DashboardServer(0);
        Assert.True(server.Start());
        try
        {
            var port = server.ActualPort;
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port);
            var ns = tcp.GetStream();
            var req = Encoding.ASCII.GetBytes($"GET /api/stream?k={server.Token} HTTP/1.1\r\nHost: x\r\n\r\n");
            await ns.WriteAsync(req);

            var reader = new StreamReader(ns, Encoding.UTF8, false, 4096, leaveOpen: true);
            var head = new StringBuilder();
            for (var i = 0; i < 12; i++)
            {
                var l = await reader.ReadLineAsync();
                if (l is null) break;
                if (l.Length == 0) break;
                head.AppendLine(l);
            }
            Assert.Contains("200 OK", head.ToString());
            Assert.Contains("text/event-stream", head.ToString());

            var first = await reader.ReadLineAsync();
            Assert.StartsWith("data: {\"type\":\"snapshot\"", first ?? "");

            server.PushTranscript("ultron", "live event hello");
            var deadline = DateTime.UtcNow.AddSeconds(6);
            string? line = null;
            while (DateTime.UtcNow < deadline)
            {
                var pending = reader.ReadLineAsync();
                var done = await Task.WhenAny(pending, Task.Delay(1200));
                if (done != pending) continue;
                line = await pending;
                if (line is not null && line.Contains("live event hello")) break;
            }
            Assert.Contains("live event hello", line ?? "");
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public void LanUrl_Is_Well_Formed()
    {
        var u = DashboardServer.LanUrl(8123);
        Assert.StartsWith("http://", u);
        Assert.EndsWith(":8123/", u);
    }
}