using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ultron.Services;

public sealed class AppSettings
{
    private static readonly string BaseDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ultron");

    private static readonly string ConfigPath = Path.Combine(BaseDir, "config.dat");
    private static bool _configCorrupt;

    public string ZenApiKey { get; set; } = ""; // legacy, kept for migration
    public string GeminiApiKey { get; set; } = "";
    public string PrimaryModel { get; set; } = "gemini-2.5-flash-native-audio-preview-12-2025";
    public List<string> FallbackModels { get; set; } = [];
    public string GeminiVoice { get; set; } = "Charon";
    public string PinHash { get; set; } = "";
    public bool VoiceEnrolled { get; set; }
    public string VoiceProfile { get; set; } = "";
    public bool WakeWordEnabled { get; set; } = true;

    // Ultron voice processor applied to every Gemini response.
    public bool VoiceDspEnabled { get; set; } = false;
    public double VoiceDspPitch { get; set; } = -3.5;   // semitones (neg = deeper)
    public double VoiceDspChorus { get; set; } = 0.3;
    public double VoiceDspBass { get; set; } = 5.0;     // dB
    public double VoiceDspDarken { get; set; } = 0.5;
    public bool SetupComplete { get; set; }
    // Global hotkeys ("Modifier+Modifier+Key", e.g. "Win+Alt+P"). Parsed by HotkeyService.
    // Defaults chosen to avoid Windows' own combos (Win+Alt+K = live captions, etc.).
    public string HotkeyPtt { get; set; } = "Win+Alt+P";
    public string HotkeyMute { get; set; } = "Win+Alt+U";
    public string HotkeyWake { get; set; } = "Win+Alt+L";
    public string WakePhrases { get; set; } = "Hey Ultron;Yo Ultron;Morning Ultron";
    public string PhoneAddr { get; set; } = "";
    public int PhonePort { get; set; } = 5555;
    public string PhoneSerial { get; set; } = "";
    public string PhonePin { get; set; } = "";
    public string Number { get; set; } = "";
    public int EngagedTimeoutSeconds { get; set; } = 120;
    public int WakeListeningTimeoutSeconds { get; set; } = 10;

    // LAN web deck: live read-only transcript + voice status, guarded by a random
    // session token baked into the QR. Default off — conversations are sensitive.
    public bool WebDashboardEnabled { get; set; } = false;
    public int WebDashboardPort { get; set; } = 8123;

    // Privacy: conversations are logged to memory.db only while this is true.
    public bool MemoryLogging { get; set; } = true;

    // Redact API keys/tokens before any text is written to the local memory store.
    public bool MemoryRedactSecrets { get; set; } = true;
    public bool MicMuted { get; set; }

    // Log verbosity: "Error", "Warn", "Info", "Debug". Everything below is filtered out.
    public string LogLevel { get; set; } = "Info";

    public bool AwayMode { get; set; }
    public string NotifyChannel { get; set; } = "telegram"; // telegram | sms | none
    public string NotifyNumber { get; set; } = "";
    public bool NotifyAtHome { get; set; }
    public string TelegramBotToken { get; set; } = "";
    public string TelegramChatId { get; set; } = "";

    // Telegram voice-call account (dedicated ULTRON user account, MTProto).
    public bool TelegramCallEnabled { get; set; }      // off by default: no impact on existing behavior
    public string TelegramApiId { get; set; } = "";
    public string TelegramApiHash { get; set; } = "";
    public string TelegramPhone { get; set; } = "";
    // Call target (@username or phone). The numeric TelegramCallUserId is cached
    // after the first successful resolution so later calls skip re-resolution.
    public string TelegramCallTarget { get; set; } = "";
    public string TelegramCallUserId { get; set; } = "";

    // Call fallback messaging: if a call goes unanswered or is hung up immediately,
    // send the payload as a Telegram message from the ULTRON account.
    public bool CallFallbackEnabled { get; set; } = true;  // on by default
    public int CallFallbackThresholdSeconds { get; set; } = 5;  // max duration for "immediate hangup"
    public string CallFallbackChatId { get; set; } = "";  // optional override target

    public bool NotifyGuardian { get; set; } = true;
    public bool NotifyReminders { get; set; } = true;
    public bool NotifyMonitor { get; set; } = true;
    public bool NotifyAwayActivity { get; set; } = true;
    public bool NotifyMissions { get; set; } = true;

public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var decrypted = ProtectedData.Unprotect(File.ReadAllBytes(ConfigPath), null, DataProtectionScope.CurrentUser);
                var s = JsonSerializer.Deserialize<AppSettings>(Encoding.UTF8.GetString(decrypted));
                if (s is not null)
                {
                    s.ZenApiKey = Environment.GetEnvironmentVariable("ZEN_API_KEY")?.Trim() ??
                                  s.ZenApiKey.Trim();
                    s.GeminiApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")?.Trim() ??
                                     s.GeminiApiKey.Trim();
                    s.TelegramBotToken = Environment.GetEnvironmentVariable("ULTRON_TELEGRAM_TOKEN")?.Trim() ??
                                         s.TelegramBotToken.Trim();
                    s.TelegramChatId = Environment.GetEnvironmentVariable("ULTRON_TELEGRAM_CHAT")?.Trim() ??
                                       s.TelegramChatId.Trim();
                    s.TelegramApiId = Environment.GetEnvironmentVariable("ULTRON_TELEGRAM_API_ID")?.Trim() ??
                                      s.TelegramApiId.Trim();
                    s.TelegramApiHash = Environment.GetEnvironmentVariable("ULTRON_TELEGRAM_API_HASH")?.Trim() ??
                                        s.TelegramApiHash.Trim();
                    s.TelegramPhone = Environment.GetEnvironmentVariable("ULTRON_TELEGRAM_PHONE")?.Trim() ??
                                      s.TelegramPhone.Trim();
                    s.TelegramCallTarget = Environment.GetEnvironmentVariable("ULTRON_TELEGRAM_TARGET")?.Trim() ??
                                           s.TelegramCallTarget.Trim();
                     var loadedCallEnabled = Environment.GetEnvironmentVariable("ULTRON_TELEGRAM_ENABLED");
                     if (loadedCallEnabled is not null)
                         s.TelegramCallEnabled = ParseBool(loadedCallEnabled);
                     var loadedFallbackEnabled = Environment.GetEnvironmentVariable("ULTRON_CALL_FALLBACK_ENABLED");
                     if (loadedFallbackEnabled is not null)
                         s.CallFallbackEnabled = ParseBool(loadedFallbackEnabled);
                    int.TryParse(Environment.GetEnvironmentVariable("ULTRON_CALL_FALLBACK_THRESHOLD"), out var thr);
                    if (thr > 0) s.CallFallbackThresholdSeconds = thr;
                    s.CallFallbackChatId = Environment.GetEnvironmentVariable("ULTRON_CALL_FALLBACK_CHAT_ID")?.Trim() ??
                                          s.CallFallbackChatId.Trim();
                     _configCorrupt = false;
                     return s;
                }
            }
        }
        catch (JsonException)
        {
            _configCorrupt = true;
        }
        catch (CryptographicException)
        {
            _configCorrupt = false;
        }
        catch (IOException)
        {
            _configCorrupt = false;
        }
        catch
        {
            _configCorrupt = false;
        }
        var fresh = new AppSettings();
        fresh.ZenApiKey = Environment.GetEnvironmentVariable("ZEN_API_KEY")?.Trim() ?? "";
        fresh.GeminiApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")?.Trim() ?? "";
        fresh.TelegramBotToken = Environment.GetEnvironmentVariable("ULTRON_TELEGRAM_TOKEN")?.Trim() ?? "";
        fresh.TelegramChatId = Environment.GetEnvironmentVariable("ULTRON_TELEGRAM_CHAT")?.Trim() ?? "";
        fresh.TelegramApiId = Environment.GetEnvironmentVariable("ULTRON_TELEGRAM_API_ID")?.Trim() ?? "";
        fresh.TelegramApiHash = Environment.GetEnvironmentVariable("ULTRON_TELEGRAM_API_HASH")?.Trim() ?? "";
        fresh.TelegramPhone = Environment.GetEnvironmentVariable("ULTRON_TELEGRAM_PHONE")?.Trim() ?? "";
        fresh.TelegramCallTarget = Environment.GetEnvironmentVariable("ULTRON_TELEGRAM_TARGET")?.Trim() ?? "";
         var freshCallEnabled = Environment.GetEnvironmentVariable("ULTRON_TELEGRAM_ENABLED");
         if (freshCallEnabled is not null)
             fresh.TelegramCallEnabled = ParseBool(freshCallEnabled);
         var freshFallbackEnabled = Environment.GetEnvironmentVariable("ULTRON_CALL_FALLBACK_ENABLED");
         if (freshFallbackEnabled is not null)
             fresh.CallFallbackEnabled = ParseBool(freshFallbackEnabled);
        int.TryParse(Environment.GetEnvironmentVariable("ULTRON_CALL_FALLBACK_THRESHOLD"), out var thr2);
        if (thr2 > 0) fresh.CallFallbackThresholdSeconds = thr2;
        fresh.CallFallbackChatId = Environment.GetEnvironmentVariable("ULTRON_CALL_FALLBACK_CHAT_ID")?.Trim() ?? "";
        return fresh;
    }

    public void Save()
    {
        if (_configCorrupt)
        {
            var quarantine = ConfigPath + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (File.Exists(ConfigPath)) File.Move(ConfigPath, quarantine, true);
            _configCorrupt = false;
        }
        Directory.CreateDirectory(BaseDir);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this));
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        var temporary = Path.Combine(BaseDir, $".{Path.GetRandomFileName()}.tmp");
        try
        {
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, ConfigPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void SetPin(string pin)
    {
        PinHash = HashPin(pin);
    }

    public bool VerifyPin(string pin)
    {
        return !string.IsNullOrEmpty(pin) && !string.IsNullOrEmpty(PinHash) && HashPin(pin) == PinHash;
    }

    private static bool ParseBool(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "1" or "true" or "yes" or "on";
    }

    public static string HashPin(string pin)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(pin));
        return Convert.ToHexString(hash);
    }

    public static string DataDir()
    {
        Directory.CreateDirectory(BaseDir);
        return BaseDir;
    }
}