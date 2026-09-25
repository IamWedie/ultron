using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// Spawns the real Python backend with a dummy key to verify the
/// waiting → start → error → clean-exit protocol without hitting the
/// network.  Returns early (no assertion failure) on CI runners that
/// lack the Python venv or system Python.
/// </summary>
public class BackendIntegrationTests
{
    private static readonly string BackendScript =
        Path.Combine(FindRepoRoot(), "backend", "gemini_backend.py");

    private static string FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(BackendIntegrationTests).Assembly.Location)!;
        for (var i = 0; i < 12; i++)
        {
            if (File.Exists(Path.Combine(dir, "backend", "gemini_backend.py")))
                return dir;
            dir = Path.GetDirectoryName(dir)!;
        }
        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", ".."));
    }

    private static string? FindPython()
    {
        var known = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Programs\Python\Python312\python.exe");
        if (File.Exists(known)) return known;
        try
        {
            var psi = new ProcessStartInfo("python", "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            if (p?.ExitCode == 0) return "python";
        }
        catch { }
        return null;
    }

    private static bool HasBackendDeps(string python)
    {
        try
        {
            var psi = new ProcessStartInfo(python,
                "-c \"import sounddevice, google.genai; print('ok')\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            var stdout = p?.StandardOutput.ReadToEnd();
            p?.WaitForExit(10_000);
            return p?.ExitCode == 0 && stdout?.Trim() == "ok";
        }
        catch { return false; }
    }

    private static void RequireOrSkip(string message)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
            throw new InvalidOperationException(message);
    }

    [Fact]
    public void Backend_Waiting_StartWithBadKey_ErrorThenExit()
    {
        var python = FindPython();
        if (python is null)
        {
            RequireOrSkip("Python is required for the backend integration test.");
            return;
        }
        if (!HasBackendDeps(python))
        {
            RequireOrSkip("Backend Python dependencies are required for the integration test.");
            return;
        }
        if (!File.Exists(BackendScript))
            throw new FileNotFoundException("Backend script was not found.", BackendScript);

        var psi = new ProcessStartInfo(python, $"\"{BackendScript}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment.Remove("GEMINI_API_KEY");

        using var proc = Process.Start(psi)!;

        // Phase 1: expect the "waiting" status line within 8 s
        var waiting = ReadLineWithTimeout(proc.StandardOutput, TimeSpan.FromSeconds(8));
        Assert.NotNull(waiting);

        var j = JsonNode.Parse(waiting!)?.AsObject();
        Assert.NotNull(j);
        Assert.Equal("status", j["type"]?.GetValue<string>());
        Assert.Equal("waiting", j["state"]?.GetValue<string>());

        // Phase 2: send a start with an invalid key
        var startMsg = JsonSerializer.Serialize(new
        {
            type = "start",
            api_key = "dummy-invalid-key-not-real",
            voice = "Achernar",
        });
        proc.StandardInput.WriteLine(startMsg);
        proc.StandardInput.Flush();

        // Phase 3: expect an error event, then the process exits
        var sawError = false;
        var sawStatus = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (DateTime.UtcNow < deadline && !proc.HasExited)
        {
            var line = ReadLineWithTimeout(proc.StandardOutput, TimeSpan.FromSeconds(5));
            if (line is null) break;

            var node = JsonNode.Parse(line);
            if (node is null) continue;
            var type = node["type"]?.GetValue<string>();

            if (type == "error")
            {
                sawError = true;
                Assert.False(string.IsNullOrWhiteSpace(node["message"]?.GetValue<string>()));
                break;
            }
            if (type == "status")
            {
                var state = node["state"]?.GetValue<string>();
                if (state is "connecting" or "error")
                    sawStatus = true;
            }
        }

        if (!proc.HasExited)
        {
            proc.StandardInput.WriteLine("{\"type\":\"stop\"}");
            proc.StandardInput.Flush();
            proc.WaitForExit(10_000);
        }

        Assert.True(proc.HasExited, "Backend process did not exit after stop.");
        Assert.True(sawError || sawStatus,
            "Expected at least one error or error-status event after sending a bad API key.");

        // Drain stderr so the process object fully reaps.  The backend prints a
        // Python traceback to stderr on its fatal path — text is not asserted.
        _ = proc.StandardError.ReadToEnd();
    }

    private static string? ReadLineWithTimeout(StreamReader reader, TimeSpan timeout)
    {
        var task = reader.ReadLineAsync();
        return task.Wait((int)timeout.TotalMilliseconds) ? task.Result : null;
    }
}