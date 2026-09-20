using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

public class OutreachTests
{
    [Fact]
    public async Task SendAsync_NotAway_Suppressed()
    {
        var s = new AppSettings { NotifyChannel = "telegram", TelegramBotToken = "x", TelegramChatId = "1" };
        using var o = new Outreach(s);
        var r = await o.SendAsync("hello", "guardian");
        Assert.StartsWith("(suppressed", r);
    }

    [Fact]
    public async Task SendAsync_Away_NoChannel_Unusable()
    {
        var s = new AppSettings { AwayMode = true, NotifyChannel = "sms" };
        using var o = new Outreach(s);
        var r = await o.SendAsync("hello", "guardian");
        Assert.StartsWith("(not sent", r);
    }

    [Fact]
    public async Task SendAsync_Away_AreaToggledOff_Suppressed()
    {
        var s = new AppSettings { AwayMode = true, NotifyChannel = "telegram", NotifyGuardian = false };
        using var o = new Outreach(s);
        var r = await o.SendAsync("hello", "guardian");
        Assert.StartsWith("(suppressed", r);
    }

    [Fact]
    public async Task SendAsync_Away_SmsDelivered()
    {
        var s = new AppSettings { AwayMode = true, NotifyChannel = "sms", NotifyNumber = "+123" };
        using var o = new Outreach(s);
        o.RegisterSmsSender((num, msg) => { Assert.Equal("+123", num); return "queued"; });
        var r = await o.SendAsync("hello");
        Assert.StartsWith("SMS: queued", r);
    }

    [Fact]
    public async Task SendAsync_Away_TelegramUnconfigured_FallsBackToSms()
    {
        var s = new AppSettings { AwayMode = true, NotifyChannel = "telegram", NotifyNumber = "+123" };
        using var o = new Outreach(s);
        o.RegisterSmsSender((num, msg) => "acked");
        var r = await o.SendAsync("hello");
        Assert.StartsWith("(telegram unavailable)", r);
    }

    [Fact]
    public async Task SendAsync_Away_TelegramConfigured_SendsViaApi()
    {
        var s = new AppSettings { AwayMode = true, NotifyChannel = "telegram", TelegramBotToken = "TOKEN", TelegramChatId = "123456" };
        using var http = new HttpClient(new OkHandler());
        using var o = new Outreach(s, http);
        var r = await o.SendAsync("hello", "monitor");
        Assert.Equal("Sent via Telegram", r);
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true}""") });
    }
}