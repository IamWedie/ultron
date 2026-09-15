using System.Net;

namespace Ultron.Services;

public sealed class ModelRepo
{
    private readonly string _dir;
    public event Action<string, string, double>? Progress;

    public string Dir => _dir;

    public ModelRepo(string baseDir)
    {
        _dir = Path.Combine(baseDir, "models");
        Directory.CreateDirectory(_dir);
    }

    public string PathFor(string key) => Path.Combine(_dir, key);

    public bool Has(string key) => File.Exists(PathFor(key)) && new FileInfo(PathFor(key)).Length > 0;

    private static readonly Dictionary<string, string> Sources = new()
    {
        ["whisper-encoder.onnx"] = "https://huggingface.co/onnx-community/whisper-tiny.en/resolve/main/onnx/encoder_model_quantized.onnx",
        ["whisper-decoder.onnx"] = "https://huggingface.co/onnx-community/whisper-tiny.en/resolve/main/onnx/decoder_model_merged_quantized.onnx",
        ["whisper-tokenizer.json"] = "https://huggingface.co/onnx-community/whisper-tiny.en/resolve/main/tokenizer.json",
        ["silero-vad.onnx"] = "https://raw.githubusercontent.com/snakers4/silero-vad/master/src/silero_vad/data/silero_vad.onnx",
        ["kokoro-v1.0.int8.onnx"] = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.1/kokoro-v1.0.int8.onnx",
        ["kokoro-voices-v1.0.bin"] = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.1/voices-v1.0.bin",
    };

    public async Task<string> EnsureAsync(string key)
    {
        if (Has(key)) return PathFor(key);
        if (!Sources.TryGetValue(key, out var url))
            throw new InvalidOperationException($"Unknown model: {key}");
        var dest = PathFor(key);
        var tmp = dest + ".tmp";
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ULTRON/1.0 (framework)");
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Download failed ({resp.StatusCode}): {key}");
        var total = resp.Content.Headers.ContentLength ?? -1;
        await using (var src = await resp.Content.ReadAsStreamAsync())
        {
            await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            var buffer = new byte[1 << 16];
            long done = 0;
            while (true)
            {
                var n = await src.ReadAsync(buffer);
                if (n == 0) break;
                await fs.WriteAsync(buffer.AsMemory(0, n));
                done += n;
                if (total > 0)
                    Progress?.Invoke(key, FormatSize(done), (double)done / total);
            }
            await fs.FlushAsync();
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { File.Move(tmp, dest, true); break; }
            catch (IOException) when (attempt < 2) { await Task.Delay(250); }
        }
        if (File.Exists(tmp))
        {
            var srcBytes = await File.ReadAllBytesAsync(tmp);
            await File.WriteAllBytesAsync(dest, srcBytes);
            File.Delete(tmp);
        }
        return dest;
    }

    public async Task<string> EnsureAllAsync()
    {
        foreach (var key in Sources.Keys)
            if (!Has(key))
                await EnsureAsync(key);
        return _dir;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B",
    };
}