using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ultron.Models;

namespace Ultron.Services;

public sealed class ZenApiClient : IDisposable
{
    public const string BaseUrl = "https://opencode.ai/zen/v1";
    private readonly HttpClient _http;
    private readonly string _sessionId = $"ses_{Guid.NewGuid():N}";
    private bool _disposed;

    public ZenApiClient(string apiKey)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "opencode/1.15.0 ai-sdk/provider-utils/4.0.23 runtime/bun/1.3.13");
        _http.DefaultRequestHeaders.Add("x-opencode-client", "cli");
        _http.DefaultRequestHeaders.Add("x-opencode-project", "global");
        _http.DefaultRequestHeaders.Add("x-opencode-session", _sessionId);
    }

    public static string FriendlyError(string raw)
    {
        var s = raw.ToLowerInvariant();
        if (s.Contains("401") || (s.Contains("invalid") && s.Contains("api")) || s.Contains("authentication") || s.Contains("incorrect api key"))
            return "Your Zen API key was rejected (error 401). Enter a valid key from opencode.ai/auth in Settings.";
        if (s.Contains("429") || s.Contains("rate limit"))
            return "Too many requests — the Zen API rate limit was hit. Wait a minute and try again.";
        if (s.Contains("403"))
            return "Access denied by the Zen API. Your key may not have access to this model.";
        if (s.Contains("502") || s.Contains("bad gateway"))
            return "The Zen API is temporarily unreachable. Try again in a few seconds.";
        if (s.Contains("timeout"))
            return "The request timed out. Check your internet connection and try again.";
        if (s.Contains("connection") || s.Contains("connect"))
            return "Could not reach the Zen API. Check your internet connection.";
        if (s.Contains("not found") || s.Contains("404"))
            return "That model was not found. It may have been renamed — try a different model in Settings.";
        var m = raw.Trim();
        return string.IsNullOrEmpty(m) ? "Something went wrong. Try again, or restart ULTRON." : m[..Math.Min(300, m.Length)];
    }

    public async Task<List<ModelInfo>> ListModelsAsync()
    {
        using var resp = await _http.GetAsync($"{BaseUrl}/models");
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(FriendlyError(body));
        var json = JsonNode.Parse(body);
        var arr = json?["data"]?.AsArray();
        var models = new List<ModelInfo>();
        if (arr is null) return models;
        foreach (var node in arr)
        {
            var id = node?["id"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(id)) continue;
            var free = id.EndsWith("-free", StringComparison.Ordinal) || id is "big-pickle" or "hy3-free" or "x-preview-f-free";
            var label = id;
            models.Add(new ModelInfo(id, label, free));
        }
        return models.OrderByDescending(m => m.Free).ThenBy(m => m.Label.ToLowerInvariant()).ToList();
    }

    public async Task<StreamResult> CompleteAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDef>? tools,
        double temperature = 0.4,
        Action<string>? onChunk = null)
    {
        var payload = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = temperature,
            ["stream"] = true,
        };
        var msgArr = new JsonArray();
        foreach (var m in messages) msgArr.Add(m.ToJson());
        payload["messages"] = msgArr;
        if (tools is { Count: > 0 })
        {
            var defs = new JsonArray();
            foreach (var t in tools) defs.Add(t.ToJson());
            payload["tools"] = defs;
        }

        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("x-opencode-request", $"msg_{Guid.NewGuid():N}");
            try
            {
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode)
                {
                    var ebody = await resp.Content.ReadAsStringAsync();
                    var friendly = FriendlyError(ebody);
                    if ((int)resp.StatusCode == 429 && attempt < 2)
                    {
                        last = new InvalidOperationException(friendly);
                        await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)));
                        continue;
                    }
                    throw new InvalidOperationException(friendly);
                }
                using var stream = await resp.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return await ParseSseAsync(reader, onChunk);
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                last = new InvalidOperationException("Could not reach the Zen API. Check your internet connection.");
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)));
            }
        }
        throw last ?? new InvalidOperationException("Something went wrong. Try again, or restart ULTRON.");
    }

    public static async Task<StreamResult> ParseSseAsync(TextReader reader, Action<string>? onChunk = null)
    {
        var result = new StreamResult();
        var toolAcc = new Dictionary<int, (string id, string name, string args)>();
        var content = "";
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            JsonNode? chunk;
            try { chunk = JsonNode.Parse(data); }
            catch { continue; }
            var choice = chunk?["choices"]?.AsArray()?.FirstOrDefault();
            if (choice is null) continue;

            var deltaDesc = choice["delta"]?.AsObject();
            if (deltaDesc is null) continue;
            var delta = deltaDesc["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(delta))
            {
                content += delta;
                try { onChunk?.Invoke(delta); } catch { }
            }
            var toolCalls = deltaDesc["tool_calls"]?.AsArray();
            if (toolCalls is not null)
            {
                foreach (var tcn in toolCalls)
                {
                    var idx = tcn?["index"]?.GetValue<int>() ?? 0;
                    if (!toolAcc.TryGetValue(idx, out var acc))
                    {
                        acc = ("", "", "");
                        toolAcc[idx] = acc;
                    }
                    var id = tcn?["id"]?.GetValue<string>() ?? "";
                    var fnName = tcn?["function"]?["name"]?.GetValue<string>() ?? "";
                    var fnArgs = tcn?["function"]?["arguments"]?.GetValue<string>() ?? "";
                    toolAcc[idx] = (
                        acc.id + (string.IsNullOrEmpty(id) ? acc.id : id),
                        acc.name + (string.IsNullOrEmpty(fnName) ? acc.name : fnName),
                        acc.args + fnArgs);
                }
            }
            var finish = choice["finish_reason"]?.GetValue<string>();
            if (finish is "tool_calls" or "stop") break;
        }

        result.Content = content;
        if (toolAcc.Count > 0)
        {
            var calls = new List<ToolCall>();
            foreach (var idx in toolAcc.Keys.OrderBy(i => i))
            {
                var (id, name, args) = toolAcc[idx];
                calls.Add(new ToolCall(id.Length > 0 ? id : $"call_{idx}", name, string.IsNullOrEmpty(args) ? "{}" : args));
            }
            result.ToolCalls = calls;
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}