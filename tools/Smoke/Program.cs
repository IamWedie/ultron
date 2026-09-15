using Ultron.Services;

Console.WriteLine("ULTRON smoke test");
Console.WriteLine("================\n");

var settings = AppSettings.Load();
Console.WriteLine($"key loaded: {(!string.IsNullOrEmpty(settings.ZenApiKey) ? "yes (" + settings.ZenApiKey[..8] + "...)" : "NO")}");
Console.WriteLine($"primary model: {settings.PrimaryModel}");
Console.WriteLine($"db dir: {AppSettings.DataDir()}\n");

var memory = new MemoryStore();
var brain = new Brain(settings, memory);
brain.ApprovalRequested += req =>
{
    Console.WriteLine($"[APPROVAL] {req.Description} -> APPROVED (smoke)\n");
    req.Resolve?.Invoke(true);
};

var input = args.Length > 0 ? string.Join(" ", args) : "Say hello, briefly.";
Console.WriteLine($"USER: {input}\n");
Console.Write("ULTRON: ");
var reply = await brain.AskAsync(input, chunk => Console.Write(chunk));
Console.WriteLine("\n");

if (await TryFetchModelsAsync(settings))
{
    Console.WriteLine("model list fetch: OK");
}

async Task<bool> TryFetchModelsAsync(AppSettings s)
{
    try
    {
        var client = new ZenApiClient(s.ZenApiKey);
        var models = await client.ListModelsAsync();
        Console.WriteLine($"models: {models.Count} returned, top: {string.Join(", ", models.Take(6).Select(m => $"{m.Id}{(m.Free ? "*" : "")}"))}");
        return true;
    }
    catch (Exception e)
    {
        Console.WriteLine($"model list fetch failed: {e.Message}");
        return false;
    }
}