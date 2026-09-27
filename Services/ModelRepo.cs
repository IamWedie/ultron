using System.Net;
using System.Security.Cryptography;

namespace Ultron.Services;

public sealed class ModelRepo
{
    private readonly string _dir;
    private readonly IReadOnlyDictionary<string, ModelInfo> _models;
    public event Action<string, string, double>? Progress;

    public string Dir => _dir;

    public ModelRepo(string baseDir, IReadOnlyDictionary<string, ModelInfo>? models = null)
    {
        _dir = Path.Combine(baseDir, "models");
        _models = models ?? Catalog;
        Directory.CreateDirectory(_dir);
    }

    public string PathFor(string key) => Path.Combine(_dir, key);

    public bool Has(string key) => File.Exists(PathFor(key)) && new FileInfo(PathFor(key)).Length > 0;

    // URLs are pinned to an immutable commit rather than a branch: a branch URL
    // can start serving different bytes tomorrow, which would turn a correct
    // digest into a permanent install failure.
    private static readonly IReadOnlyDictionary<string, ModelInfo> Catalog = new Dictionary<string, ModelInfo>
    {
        ["whisper-encoder.onnx"] = new ModelInfo(
            "https://huggingface.co/onnx-community/whisper-tiny.en/resolve/2575352d61be1bf7225cf8f8b268a4678025fc58/onnx/encoder_model_quantized.onnx",
            "e93ec822f16a8fd264e7de972ad17d615ea7334b75a52d54c50c2e18dd503a25",
            10_124_993),
        ["whisper-decoder.onnx"] = new ModelInfo(
            "https://huggingface.co/onnx-community/whisper-tiny.en/resolve/2575352d61be1bf7225cf8f8b268a4678025fc58/onnx/decoder_model_merged_quantized.onnx",
            "c0592d0749413c960569e1c7fb806b060d5d18f3ebad4a95cbf9a77dc6e9be52",
            30_718_858),
        ["whisper-tokenizer.json"] = new ModelInfo(
            "https://huggingface.co/onnx-community/whisper-tiny.en/resolve/2575352d61be1bf7225cf8f8b268a4678025fc58/tokenizer.json",
            "5eb60cec1e77aeeb6869a2bb5a8e01a84c3fe5d072d75369343021fe6f5310d0",
            2_405_679),
        ["silero-vad.onnx"] = new ModelInfo(
            "https://raw.githubusercontent.com/snakers4/silero-vad/5cd7945676eb32225748052e2e6a0580e4686a08/src/silero_vad/data/silero_vad.onnx",
            "1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3",
            2_327_524),
    };

    public record ModelInfo(string Url, string ExpectedSha256, long ExpectedSize);

    public async Task<string> EnsureAsync(string key)
    {
        if (Has(key))
        {
            if (await VerifyIntegrityAsync(key)) return PathFor(key);

            // Quarantine rather than delete. If the expected digest is ever wrong,
            // deleting here would destroy a perfectly good multi-megabyte download
            // and re-fetch it on every launch.
            Quarantine(key);
        }
        if (!_models.TryGetValue(key, out var model))
            throw new InvalidOperationException($"Unknown model: {key}");
        var dest = PathFor(key);
        var tmp = dest + ".tmp";
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ULTRON/1.0 (framework)");
        using var resp = await client.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead);
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Download failed ({resp.StatusCode}): {key}");
        var total = resp.Content.Headers.ContentLength ?? -1;
        if (model.ExpectedSize > 0 && total > 0 && total != model.ExpectedSize)
            throw new InvalidOperationException($"Size mismatch for {key}: expected {model.ExpectedSize}, got {total}");
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
        // Verify SHA-256 before committing
        if (!await VerifyFileSha256Async(tmp, model.ExpectedSha256))
            throw new InvalidOperationException($"SHA-256 verification failed for {key}");
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

    private void Quarantine(string key)
    {
        var path = PathFor(key);
        try
        {
            File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot set it aside (file in use, read-only). Fall back to deleting so
            // the download below can still make progress.
            try { File.Delete(path); } catch { }
        }
    }

    private async Task<bool> VerifyIntegrityAsync(string key)
    {
        if (!_models.TryGetValue(key, out var model)) return false;
        return await VerifyFileSha256Async(PathFor(key), model.ExpectedSha256);
    }

    private static async Task<bool> VerifyFileSha256Async(string path, string expectedSha256)
    {
        try
        {
            using var sha256 = SHA256.Create();
            await using var fs = File.OpenRead(path);
            var hash = await sha256.ComputeHashAsync(fs);
            var actual = Convert.ToHexString(hash).ToLowerInvariant();
            return actual == expectedSha256.ToLowerInvariant();
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> EnsureAllAsync()
    {
        // Unconditional: EnsureAsync verifies an already-present file and returns
        // immediately when the digest matches, so this both verifies on every
        // startup and repairs anything that no longer matches.
        foreach (var key in _models.Keys)
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