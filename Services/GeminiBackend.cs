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

    public bool IsConnected => _proc is { HasExited: false };
    public string State { get; private set; } = "disconnected";
    public string BackendVersion { get; private set; } = "";
    public IReadOnlySet<string> BackendCapabilities { get; private set; } = new HashSet<string>();
    public PersonaSettings Persona { get; private set; } = new();

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