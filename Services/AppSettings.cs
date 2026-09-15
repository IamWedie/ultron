using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ultron.Services;

public sealed class AppSettings
{
    private static readonly string BaseDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ultron");

    private static readonly string ConfigPath = Path.Combine(BaseDir, "config.dat");

    public string ZenApiKey { get; set; } = ""; // legacy, kept for migration
    public string GeminiApiKey { get; set; } = "";
    public string PrimaryModel { get; set; } = "gemini-2.5-flash-native-audio-preview-12-2025";
    public List<string> FallbackModels { get; set; } = [];
    public string GeminiVoice { get; set; } = "Charon";
    public string TtsVoice { get; set; } = "bm_lewis";
    public string TtsRate { get; set; } = "+30%";
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

    // Log verbosity: "Error", "Warn", "Info", "Debug". Everything below is filtered out.
    public string LogLevel { get; set; } = "Info";

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
                    return s;
                }
            }
        }
        catch { }
        var fresh = new AppSettings();
        fresh.ZenApiKey = Environment.GetEnvironmentVariable("ZEN_API_KEY")?.Trim() ?? "";
        fresh.GeminiApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")?.Trim() ?? "";
        return fresh;
    }

    public void Save()
    {
        Directory.CreateDirectory(BaseDir);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this));
        File.WriteAllBytes(ConfigPath, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
    }

    public void SetPin(string pin)
    {
        PinHash = HashPin(pin);
    }

    public bool VerifyPin(string pin)
    {
        return !string.IsNullOrEmpty(pin) && !string.IsNullOrEmpty(PinHash) && HashPin(pin) == PinHash;
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