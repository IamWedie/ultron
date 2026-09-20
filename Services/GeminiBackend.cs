using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Ultron.Services;

public sealed class GeminiToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Dictionary<string, object> Args { get; init; }
}

/// <summary>Personality dials shipped to the Gemini backend as live system
/// instructions. Defaults preserve the existing detached Ultron tone.</summary>
public sealed class PersonaSettings
{
    /// <summary>-2 serious … +2 playful; 0 keeps the default tone.</summary>
    public int Humor { get; init; } = 0;
    /// <summary>0 ultra-short, 1 balanced, 2 verbose.</summary>
    public int Length { get; init; } = 1;
    /// <summary>0 only when asked, 1 moderate, 2 proactive.</summary>
    public int Suggest { get; init; } = 1;
    /// <summary>Free-text standing instructions/preferences.</summary>
    public string Notes { get; init; } = "";

    internal object ToPayload() => new
    {
        humor = Humor,
        length = Length,
        suggest = Suggest,
        notes = Notes,
    };
}

public sealed record ContactRequest(string Id, string Area, string Text, string Priority, bool Ask, string ReplyId);

/// <summary>Telegram call-account credentials handed to the backend process via
/// environment variables. api_hash is kept only in memory / DPAPI-protected config.</summary>
public sealed record TelegramCallOptions(string ApiId, string ApiHash, string Phone, bool Enabled, string Target);

/// <summary>Outcome of resolving the configured Telegram call target to a user account.</summary>
public sealed record TelegramTargetResult(bool Ok, string Message, string UserId, string Name, string Target);

public sealed class GeminiBackend : IDisposable
{
    private static readonly string BackendScript =
        Path.Combine(AppContext.BaseDirectory, "backend", "gemini_backend.py");

    private static void Dbg(string msg)
    {
        AppLog.Write("GeminiBackend", msg);
    }

    private static void Dbg(string msg, AppLog.Level level)
    {
        AppLog.Write("GeminiBackend", msg, level);
    }

    private Process? _proc;
    private CancellationTokenSource? _runCts;
    private bool _disposed;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<string>> _pendingTools = new();

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ToolCallTimeout = TimeSpan.FromSeconds(30);

    public event Action<string>? StatusChanged;
    public event Action<string, string>? TranscriptReceived; // role, text
    public event Action<GeminiToolCall>? ToolCallReceived;
    public event Action<string, JsonElement>? ComputerActRequested; // id, action
    public event Action<string>? ErrorOccurred;
    public event Action? Disconnected;
    public event Action<ContactRequest>? ContactRequested;
    public event Action<string, string, bool>? TelegramStatusChanged; // phase, message, available
    public event Action<string, string>? TelegramCodeRequired;        // phone, hint
    public event Action<bool, string>? TelegramCodeResult;            // ok, message
    public event Action<TelegramTargetResult>? TelegramTargetResolved;
    public event Action<string, string>? TelegramCallStateChanged;    // state, message
    public event Action<bool, string>? TelegramCallResult;            // ok, message
    public event Action<string, string, string>? TelegramCallFallbackSent; // source, target, reason

    public bool IsConnected => _proc is { HasExited: false };
    public string State { get; private set; } = "disconnected";
    public string BackendVersion { get; private set; } = "";
    public IReadOnlySet<string> BackendCapabilities { get; private set; } = new HashSet<string>();
    public PersonaSettings Persona { get; private set; } = new();
    public TelegramCallOptions? TelegramOptions { get; set; }

    public event Action<string>? BackendInfoReceived; // "version vX — cap1, cap2"

    public async Task StartAsync(string apiKey, string voice = "Charon")
    {
        if (_proc is { HasExited: false })
        {
            Dbg("StartAsync: already running, stopping first");
            Stop();
        }

        var pythonExe = FindPython();
        if (string.IsNullOrEmpty(pythonExe))
        {
            ErrorOccurred?.Invoke("Python not found. Install Python 3.11+ and add to PATH.");
            return;
        }

        if (!File.Exists(BackendScript))
        {
            ErrorOccurred?.Invoke($"Backend script not found: {BackendScript}");
            return;
        }

        Dbg($"StartAsync: python={pythonExe}, voice={voice}");

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = $"\"{BackendScript}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["GEMINI_API_KEY"] = apiKey;
        psi.Environment["ULTRON_VOICE"] = voice;
        psi.Environment["ULTRON_TELEGRAM_API_ID"] = TelegramOptions?.ApiId ?? "";
        psi.Environment["ULTRON_TELEGRAM_API_HASH"] = TelegramOptions?.ApiHash ?? "";
        psi.Environment["ULTRON_TELEGRAM_TARGET"] = TelegramOptions?.Target ?? "";
        psi.Environment["ULTRON_TELEGRAM_PHONE"] = TelegramOptions?.Phone ?? "";
        psi.Environment["ULTRON_TELEGRAM_ENABLED"] = TelegramOptions is { Enabled: true } ? "1" : "0";

        _proc = Process.Start(psi);
        if (_proc is null)
        {
            ErrorOccurred?.Invoke("Failed to start Python backend process.");
            return;
        }

        _proc.EnableRaisingEvents = true;
        _proc.Exited += OnProcessExited;

        // Cancellation token scoped to this backend run so loops exit on stop/restart.
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;

        _ = Task.Run(() => ReadOutputLoop(_proc.StandardOutput, token), token);
        _ = Task.Run(() => ReadErrorLoop(_proc.StandardError, token), token);

        // Send start command
        await SendAsync(new { type = "start", api_key = apiKey, voice, personality = Persona.ToPayload() });

        State = "connecting";
        StatusChanged?.Invoke("connecting");

        // Fail fast if the backend never completes its handshake: a stuck
        // python process should not leave the UI in "connecting" forever.
        var deadline = DateTime.UtcNow + HandshakeTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_proc.HasExited)
            {
                ErrorOccurred?.Invoke("Backend process exited before the handshake completed.");
                return;
            }
            if (BackendVersion.Length > 0) return;
            await Task.Delay(100, token);
        }
        Dbg("Handshake timeout: backend did not report version within 20s");
        ErrorOccurred?.Invoke("Backend handshake timed out.");
        await StopAsync();
    }

    public async Task StopAsync()
    {
        if (_disposed) { StopCleanup(); return; }
        _runCts?.Cancel();
        try
        {
            if (_proc is { HasExited: false })
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                    await SendAsync(new { type = "stop" }).WaitAsync(cts.Token);
                    if (!_proc.WaitForExit(3000))
                        _proc.Kill();
                }
                catch (TimeoutException) { _proc.Kill(); }
                catch { }
            }
        }
        catch { }
        finally
        {
            StopCleanup();
        }
    }

    /// <summary>Idempotent teardown: kill/dispose the process, cancel loops,
    /// fail any in-flight tool calls, unsubscribe the exit handler.</summary>
    private void StopCleanup()
    {
        if (_proc is not null)
        {
            _proc.Exited -= OnProcessExited;
            _proc.Dispose();
            _proc = null;
        }
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
        foreach (var kv in _pendingTools)
            kv.Value.TrySetResult("Backend stopped while the tool was running.");
        _pendingTools.Clear();
        State = "disconnected";
    }

    public void Stop() => _ = StopAsync();

    public async Task SendTextAsync(string text)
    {
        await SendAsync(new { type = "text", text });
    }

    public async Task InterruptAsync()
    {
        await SendAsync(new { type = "interrupt" });
    }

    public async Task SetAwakeAsync(bool on)
    {
        await SendAsync(new { type = "set_awake", on });
    }

    public async Task SetWakeEnabledAsync(bool enabled)
    {
        await SendAsync(new { type = "set_wake", enabled });
    }

    public async Task SendVoiceDspAsync(bool enabled, double semitones, double chorus, double bass, double darken)
    {
        await SendAsync(new { type = "voice_dsp", enabled, semitones, chorus, bass, darken });
    }

    public async Task<string> SendToolResultAsync(string callId, string name, string result)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingTools[callId] = tcs;
        await SendAsync(new { type = "tool_result", id = callId, name, result });

        using var cts = new CancellationTokenSource(ToolCallTimeout);
        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch
        {
            _pendingTools.Remove(callId);
            return "Tool execution timed out.";
        }
    }

    public async Task SendComputerActResultAsync(string id, string result)
    {
        await SendAsync(new { type = "computer_act_result", id, result });
    }

    public async Task SetLiveVisionAsync(bool enabled)
    {
        await SendAsync(new { type = "set_live_vision", enabled });
    }

    public async Task SetAwayAsync(bool on)
    {
        await SendAsync(new { type = "set_away", on });
    }

    public async Task SendContactReplyAsync(string replyId, bool ok, string text)
    {
        await SendAsync(new { type = "contact_reply", reply_id = replyId, ok, text });
    }

    public async Task SendTelegramLoginAsync(string? phone = null, string? apiId = null, string? apiHash = null)
    {
        await SendAsync(new
        {
            type = "telegram_login",
            api_id = apiId,
            api_hash = apiHash,
            phone,
        });
    }

    public async Task SendTelegramCodeAsync(string code)
    {
        await SendAsync(new { type = "telegram_code", code });
    }

    public async Task SendTelegramLogoutAsync()
    {
        await SendAsync(new { type = "telegram_logout" });
    }

    public async Task SendTelegramStatusAsync()
    {
        await SendAsync(new { type = "telegram_status" });
    }

    public async Task SendTelegramResolveTargetAsync(string target)
    {
        await SendAsync(new { type = "telegram_resolve_target", target });
    }

    public async Task SendTelegramCallStartAsync(object? payload = null)
    {
        await SendAsync(new { type = "telegram_call_start", payload });
    }

    public async Task SendTelegramCallStopAsync()
    {
        await SendAsync(new { type = "telegram_call_stop" });
    }

    public async Task SendTelegramCallStatusAsync()
    {
        await SendAsync(new { type = "telegram_call_status" });
    }

    /// <summary>Applies a new personality to the running backend. If the
    /// process is alive this updates the live session's system instruction
    /// (it reconnects); otherwise the value is kept and carried by the next
    /// <c>start</c>.</summary>
    public void SetPersona(PersonaSettings persona) => _ = SetPersonaAsync(persona);

    public async Task SetPersonaAsync(PersonaSettings persona)
    {
        Persona = persona;
        await SendAsync(new
        {
            type = "set_personality",
            humor = persona.Humor,
            length = persona.Length,
            suggest = persona.Suggest,
            notes = persona.Notes,
        });
    }

    private async Task SendAsync(object msg)
    {
        if (_proc is null || _proc.HasExited) return;
        await _sendGate.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(msg);
            await _proc.StandardInput.WriteLineAsync(json);
            await _proc.StandardInput.FlushAsync();
        }
        catch (Exception ex)
        {
            Dbg($"SendAsync error: {ex.Message}");
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReadOutputLoop(StreamReader reader, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line is null) break;
                if (string.IsNullOrEmpty(line)) continue;
                ProcessLine(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Dbg($"ReadOutputLoop error: {ex.Message}");
        }
    }

    /// <summary>Parse one IPC line from the backend. Internal (test seam): the
    /// read loop feeds every full line here; unknown/malformed messages are
    /// logged and ignored — never thrown.</summary>
    internal void ProcessLine(string line)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<JsonElement>(line);
            var type = msg.GetProperty("type").GetString() ?? "";
            HandleMessage(type, msg);
        }
        catch (Exception ex)
        {
            Dbg($"Parse error: {ex.Message} — line: {AppLog.Redact(line[..Math.Min(200, line.Length)])}");
        }
    }

    private void HandleMessage(string type, JsonElement msg)
    {
        switch (type)
        {
            case "hello":
                BackendVersion = msg.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                var caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (msg.TryGetProperty("capabilities", out var capEl) && capEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in capEl.EnumerateArray())
                        caps.Add(c.GetString() ?? "");
                }
                BackendCapabilities = caps;
                Dbg($"Backend handshake: version {BackendVersion}, capabilities: {string.Join(", ", caps)}");
                BackendInfoReceived?.Invoke($"backend v{BackendVersion} — {string.Join(", ", caps)}");
                break;

            case "status":
                var state = msg.GetProperty("state").GetString() ?? "";
                var message = msg.GetProperty("message").GetString() ?? "";
                State = state;
                StatusChanged?.Invoke(state);
                Dbg($"Status: {state} — {message}");
                break;

            case "transcript":
                var role = msg.GetProperty("role").GetString() ?? "";
                var text = msg.GetProperty("text").GetString() ?? "";
                TranscriptReceived?.Invoke(role, text);
                break;

            case "tool_call":
                var id = msg.GetProperty("id").GetString() ?? "";
                var name = msg.GetProperty("name").GetString() ?? "";
                var args = new Dictionary<string, object>();
                if (msg.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in argsEl.EnumerateObject())
                        args[prop.Name] = prop.Value.ToString();
                }
                ToolCallReceived?.Invoke(new GeminiToolCall
                {
                    Id = id,
                    Name = name,
                    Args = args,
                });
                break;

            case "error":
                var errMsg = msg.GetProperty("message").GetString() ?? "Unknown error";
                ErrorOccurred?.Invoke(errMsg);
                Dbg($"Error: {errMsg}");
                break;

            case "computer_act":
                var actId = msg.GetProperty("id").GetString() ?? "";
                if (msg.TryGetProperty("action", out var actEl) && actEl.ValueKind == JsonValueKind.Object)
                    ComputerActRequested?.Invoke(actId, actEl);
                break;

            case "contact":
                var contactText = msg.TryGetProperty("text", out var ct) ? ct.GetString() ?? "" : "";
                var contact = new ContactRequest(
                    msg.TryGetProperty("id", out var cid) ? cid.GetString() ?? "" : "",
                    msg.TryGetProperty("area", out var ca) ? ca.GetString() ?? "" : "guardian",
                    contactText,
                    msg.TryGetProperty("priority", out var cp) ? cp.GetString() ?? "" : "normal",
                    msg.TryGetProperty("ask", out var cask) ? cask.GetBoolean() : false,
                    msg.TryGetProperty("reply_id", out var cr) ? cr.GetString() ?? "" : "");
                Dbg($"Contact [{contact.Area}/{contact.Priority}]: {AppLog.Redact(contactText[..Math.Min(120, contactText.Length)])}");
                ContactRequested?.Invoke(contact);
                break;

            case "telegram_status":
                var tgPhase = msg.TryGetProperty("phase", out var tp) ? tp.GetString() ?? "" : "";
                var tgMsg = msg.TryGetProperty("message", out var tm) ? tm.GetString() ?? "" : "";
                var tgAvail = msg.TryGetProperty("available", out var ta) && ta.GetBoolean();
                TelegramStatusChanged?.Invoke(tgPhase, AppLog.Redact(tgMsg), tgAvail);
                break;

            case "telegram_code_required":
                var phone = msg.TryGetProperty("phone", out var cph) ? cph.GetString() ?? "" : "";
                var hint = msg.TryGetProperty("hint", out var ch) ? ch.GetString() ?? "" : "";
                TelegramCodeRequired?.Invoke(phone, AppLog.Redact(hint));
                break;

            case "telegram_code_result":
                var okRes = msg.TryGetProperty("ok", out var crok) && crok.GetBoolean();
                var resultMsg = msg.TryGetProperty("message", out var cmsg) ? cmsg.GetString() ?? "" : "";
                TelegramCodeResult?.Invoke(okRes, AppLog.Redact(resultMsg));
                break;

            case "telegram_target_result":
                var tOk = msg.TryGetProperty("ok", out var trok) && trok.GetBoolean();
                var tMsg = msg.TryGetProperty("message", out var trmsg) ? trmsg.GetString() ?? "" : "";
                var tId = msg.TryGetProperty("user_id", out var trid) ? trid.GetString() ?? "" : "";
                var tName = msg.TryGetProperty("name", out var trname) ? trname.GetString() ?? "" : "";
                var tTarget = msg.TryGetProperty("target", out var trt) ? trt.GetString() ?? "" : "";
                TelegramTargetResolved?.Invoke(new TelegramTargetResult(
                    tOk, AppLog.Redact(tMsg), tId, tName, tTarget));
                break;

            case "telegram_call_state":
                var csState = msg.TryGetProperty("state", out var cst) ? cst.GetString() ?? "" : "";
                var csMsg = msg.TryGetProperty("message", out var csmsg) ? csmsg.GetString() ?? "" : "";
                TelegramCallStateChanged?.Invoke(csState, AppLog.Redact(csMsg));
                break;

            case "telegram_call_result":
                var csOk = msg.TryGetProperty("ok", out var csok) && csok.GetBoolean();
                var csResMsg = msg.TryGetProperty("message", out var csrmsg) ? csrmsg.GetString() ?? "" : "";
                TelegramCallResult?.Invoke(csOk, AppLog.Redact(csResMsg));
                break;

            case "telegram_call_fallback_sent":
                var fbOk = msg.TryGetProperty("ok", out var fbo) && fbo.GetBoolean();
                var fbTarget = msg.TryGetProperty("target", out var ft) ? ft.GetString() ?? "" : "";
                var fbSource = msg.TryGetProperty("source", out var fs) ? fs.GetString() ?? "" : "";
                var fbReason = msg.TryGetProperty("reason", out var fr) ? fr.GetString() ?? "" : "";
                TelegramCallFallbackSent?.Invoke(fbSource, fbTarget, fbReason);
                break;
        }
    }

    private async Task ReadErrorLoop(StreamReader reader, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line is null) break;
                if (!string.IsNullOrEmpty(line))
                    Dbg($"stderr: {line}");
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    /// <summary>Fail any in-flight tool calls — a stuck/crashed backend must never
    /// leave the C# side awaiting a result that will never arrive.</summary>
    private void OnProcessExited(object? sender, EventArgs e)
    {
        Dbg("Process exited");
        foreach (var kv in _pendingTools)
            kv.Value.TrySetResult("Backend process exited while the tool was running.");
        _pendingTools.Clear();
        State = "disconnected";
        Disconnected?.Invoke();
    }

    private static string FindPython()
    {
        // Prioritize Python installs with pip/modules (msys64's python has no pip).
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Python", "Python312", "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Python", "Python313", "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Python", "Python311", "python.exe"),
            "python3",
            "python",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Python", "Python310", "python.exe"),
        };

        foreach (var c in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo(c, "-c \"import google.genai\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                var p = Process.Start(psi);
                if (p is not null)
                {
                    p.WaitForExit(5000);
                    if (p.ExitCode == 0)
                        return c;
                }
            }
            catch { }
        }
        return "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Deterministic shutdown: block briefly so the process really exits and
        // everything in StopCleanup runs before the semaphore is disposed.
        StopAsync().Wait(TimeSpan.FromMilliseconds(3000));
        try { _sendGate.Dispose(); }
        catch (ObjectDisposedException) { }
    }
}