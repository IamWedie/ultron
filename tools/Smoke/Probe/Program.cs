using System.Net.Http.Headers;
using System.Text;

var envKey = Environment.GetEnvironmentVariable("ZEN_API_KEY") ?? "";
using var http = new HttpClient();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", envKey);

for (var attempt = 1; attempt <= 4; attempt++)
{
    var payload = new StringContent(
        System.Text.Json.JsonSerializer.Serialize(new
        {
            model = "big-pickle",
            temperature = 0.4,
            stream = true,
            messages = new object[] { new { role = "user", content = "Say hello, briefly." } },
        }),
        Encoding.UTF8, "application/json");

    using var req = new HttpRequestMessage(HttpMethod.Post, "https://opencode.ai/zen/v1/chat/completions") { Content = payload };
    try
    {
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Console.WriteLine($"attempt {attempt}: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"   body: {body}");
        }
        else if (resp.IsSuccessStatusCode)
        {
            using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
            var n = 0;
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line is null) break;
                if (line.StartsWith("data:")) { Console.WriteLine($"   data: {(line.Length > 120 ? line[..120] + "..." : line)}"); if (++n >= 4) break; }
            }
            break;
        }
        else
        {
            Console.WriteLine($"   body: {await resp.Content.ReadAsStringAsync()}");
        }
    }
    catch (Exception e)
    {
        Console.WriteLine($"attempt {attempt} threw: {e.Message}");
    }
    await Task.Delay(3000);
}