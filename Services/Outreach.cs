using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ultron.Services;

public sealed class Outreach : IDisposable
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private Func<string, string, string?>? _smsSender;

    public Outreach(AppSettings settings, HttpClient? http = null)
    {
        _settings = settings;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
    }

    public void RegisterSmsSender(Func<string, string, string?> sender) => _smsSender = sender;

    public void Dispose() => _http.Dispose();

    public bool AnyChannelConfigured =>
        _settings.NotifyChannel != "none" &&
        (_settings.NotifyChannel == "sms"
            ? !string.IsNullOrWhiteSpace(_settings.NotifyNumber) && _smsSender is not null
            : !string.IsNullOrWhiteSpace(_settings.TelegramBotToken) &&
              !string.IsNullOrWhiteSpace(_settings.TelegramChatId));

    public async Task<string> SendAsync(string text, string area = "guardian", string priority = "normal")
    {
        if (!ShouldSend(area))
            return "(suppressed: away-mode policy)";
        var chan = (_settings.NotifyChannel ?? "telegram").ToLowerInvariant().Trim();
        if (chan == "sms")
            return await SendSmsAsync(text);
        if (chan == "telegram")
        {
            var telegram = await SendTelegramAsync(text);
            if (!string.IsNullOrEmpty(telegram))
                return telegram;
            var sms = await SendSmsAsync(text);
            return string.IsNullOrEmpty(sms) || sms.StartsWith("(not sent", StringComparison.Ordinal)
                ? "(not sent: no usable channel)"
                : $"(telegram unavailable) {sms}";
        }
        return "(not sent: no notification channel set)";
    }

    private bool ShouldSend(string area)
    {
        if (!_settings.AwayMode && !_settings.NotifyAtHome)
            return false;
        return (area?.ToLowerInvariant().Trim() ?? "guardian") switch
        {
            "guardian" => _settings.NotifyGuardian,
            "reminder" => _settings.NotifyReminders,
            "monitor" => _settings.NotifyMonitor,
            "away_activity" => _settings.NotifyAwayActivity,
            "mission" => _settings.NotifyMissions,
            _ => true,
        };
    }

    private async Task<string> SendTelegramAsync(string text)
    {
        var token = _settings.TelegramBotToken?.Trim() ?? "";
        var chat = _settings.TelegramChatId?.Trim() ?? "";
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(chat) || !long.TryParse(chat, out var chatId))
            return "";
        try
        {
            var url = $"https://api.telegram.org/bot{token}/sendMessage";
            var payload = JsonSerializer.Serialize(new
            {
                chat_id = chatId,
                text,
                disable_web_page_preview = true,
            });
            using var resp = await _http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Write("Outreach", $"Telegram send failed ({resp.StatusCode}): {AppLog.Redact(body[..Math.Min(200, body.Length)])}", AppLog.Level.Warn);
                return "";
            }
            AppLog.Write("Outreach", "Contact delivered via Telegram.");
            return "Sent via Telegram";
        }
        catch (Exception ex)
        {
            AppLog.Write("Outreach", $"Telegram send error: {ex.Message}", AppLog.Level.Warn);
            return "";
        }
    }

    private Task<string> SendSmsAsync(string text)
    {
        if (_smsSender is null)
            return Task.FromResult("(not sent: SMS sender unavailable)");
        var number = _settings.NotifyNumber?.Trim() ?? "";
        if (string.IsNullOrEmpty(number))
            return Task.FromResult("(not sent: no SMS number configured)");
        try
        {
            var result = _smsSender(number, text);
            AppLog.Write("Outreach", "Contact delivered via SMS.");
            return Task.FromResult(string.IsNullOrEmpty(result) ? "Sent via SMS" : $"SMS: {result}");
        }
        catch (Exception ex)
        {
            AppLog.Write("Outreach", $"SMS send error: {ex.Message}", AppLog.Level.Warn);
            return Task.FromResult("(not sent: SMS failed)");
        }
    }
}