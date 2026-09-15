using Ultron.Services;

Console.WriteLine("SSE parser test");
Console.WriteLine("===============\n");

var sse = """
        data: {"choices":[{"delta":{"content":"Hello"},"index":0}]}
        data: {"choices":[{"delta":{"content":", sir."},"index":0}]}
        data: {"choices":[{"delta":{},"index":0,"finish_reason":"stop"}]}
        data: [DONE]
        """;
using (var reader = new StringReader(sse))
{
    var chunks = new List<string>();
    var result = await ZenApiClient.ParseSseAsync(reader, chunks.Add);
    Console.WriteLine($"content = \"{result.Content}\"  (chunks: {chunks.Count}; toolCalls: {result.ToolCalls?.Count ?? 0})");
    var ok = result.Content == "Hello, sir." && chunks.Count == 2;
    Console.WriteLine(ok ? "PASS: plain content streaming" : "FAIL: plain content streaming");
    if (!ok) return;
}

var sseTool = """
        data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_9","function":{"name":"remember_fact","arguments":"{\"fact\":\"the user likes pink\"}"}}]},"index":0}]}
        data: {"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_10","function":{"name":"recall_memories","arguments":"{\"query\":\"favorite color\"}"}}]},"index":0}]}
        data: {"choices":[{"delta":{},"index":0,"finish_reason":"tool_calls"}]}
        data: [DONE]
        """;
using (var reader = new StringReader(sseTool))
{
    var result = await ZenApiClient.ParseSseAsync(reader, null);
    Console.WriteLine($"toolCalls = {result.ToolCalls?.Count ?? 0}");
    foreach (var tc in result.ToolCalls ?? new())
        Console.WriteLine($"  id={tc.Id} name={tc.Name} args={tc.Arguments}");
    var ok = result.ToolCalls is { Count: 2 }
             && result.ToolCalls[0].Name == "remember_fact"
             && result.ToolCalls[1].Name == "recall_memories";
    Console.WriteLine(ok ? "PASS: tool-call streaming" : "FAIL: tool-call streaming");
    if (!ok) return;
}

var ssePartial = """
        data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"type_te"}}]},"index":0}]}
        data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"xt","arguments":"{\"text\":\"dir\"}"}}]},"index":0}]}
        data: {"choices":[{"delta":{},"index":0,"finish_reason":"tool_calls"}]}
        data: [DONE]
        """;
using (var reader = new StringReader(ssePartial))
{
    var result = await ZenApiClient.ParseSseAsync(reader, null);
    var ok = result.ToolCalls is { Count: 1 }
             && result.ToolCalls[0].Name == "type_text"
             && result.ToolCalls[0].Arguments == "{\"text\":\"dir\"}";
    Console.WriteLine(ok ? "PASS: fragmented tool-call assembly" : "FAIL: fragmented tool-call assembly");
    if (!ok) return;
}

Console.WriteLine("\nAll parser tests passed.");