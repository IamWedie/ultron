using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Ultron.Services;

public sealed class KokoroTts : IDisposable
{
    public const int SampleRate = 24000;

    private InferenceSession _session = null!;
    private Dictionary<string, int> _vocab = null!;
    private Dictionary<string, float[]> _voices = null!;
    private string _voiceName = "bm_lewis";
    private readonly Dictionary<string, long[]> _tokenCache = new();
    private readonly object _tokenCacheLock = new();

    public IEnumerable<string> VoiceNames => _voices.Keys;

    public void Load(string modelDir, AppSettings settings)
    {
        var modelPath = Path.Combine(modelDir, "kokoro-v1.0.int8.onnx");
        var voicesPath = Path.Combine(modelDir, "kokoro-voices-v1.0.bin");

        _session = CreateSession(modelPath);
        _vocab = ReadEmbeddedVocab(_session);
        _voices = LoadVoicesNpz(voicesPath);

        try { File.Delete(Path.Combine(Path.GetTempPath(), "kokoro_vocab.txt")); } catch { }

        if (!string.IsNullOrWhiteSpace(settings.TtsVoice) && _voices.ContainsKey(settings.TtsVoice))
            _voiceName = settings.TtsVoice;
    }

    private static Dictionary<string, int> ReadEmbeddedVocab(InferenceSession session)
    {
        var result = new Dictionary<string, int>();
        var meta = session.ModelMetadata.CustomMetadataMap;
        if (meta != null && meta.TryGetValue("kokoro_config", out var raw) && !string.IsNullOrEmpty(raw))
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("vocab", out var v))
                foreach (var kv in v.EnumerateObject())
                    result[kv.Name] = kv.Value.GetInt32();
        }
        return result;
    }

    private static InferenceSession CreateSession(string modelPath)
    {
        var opts = new SessionOptions { IntraOpNumThreads = 12, InterOpNumThreads = 1 };
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        return new InferenceSession(modelPath, opts);
    }

    private static Dictionary<string, float[]> LoadVoicesNpz(string path)
    {
        var voices = new Dictionary<string, float[]>();
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            if (!entry.Name.EndsWith(".npy", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileNameWithoutExtension(entry.Name);
            using var s = entry.Open();
            var floats = ReadNpyFloat32(s);
            if (floats != null) voices[name] = floats;
        }
        return voices;
    }

    private static float[]? ReadNpyFloat32(Stream s)
    {
        using var reader = new BinaryReader(s);
        var magic = new byte[6];
        if (reader.Read(magic, 0, 6) != 6)
            return null;
        if (magic[0] != 0x93 || Encoding.ASCII.GetString(magic, 1, 5) != "NUMPY")
            return null;
        _ = reader.ReadByte(); // version
        _ = reader.ReadByte();
        var headerLen = reader.ReadUInt16();
        var header = Encoding.ASCII.GetString(reader.ReadBytes(headerLen));

        var i = header.IndexOf("'descr': '<f4'", StringComparison.Ordinal);
        if (i < 0) i = header.IndexOf("\"descr\": \"<f4\"", StringComparison.Ordinal);
        if (i < 0) return null; // only support float32 NPY

        var shapeStart = header.IndexOf('(', header.IndexOf("'shape'", StringComparison.Ordinal));
        if (shapeStart < 0) shapeStart = header.IndexOf('(', header.IndexOf("\"shape\"", StringComparison.Ordinal));
        var count = 1L;
        if (shapeStart >= 0)
        {
            var close = header.IndexOf(')', shapeStart);
            var inner = header.Substring(shapeStart + 1, close - shapeStart - 1).Replace(" ", "");
            foreach (var part in inner.Split(','))
                if (long.TryParse(part, out var dim) && dim > 0)
                    count *= dim;
        }
        var bytes = (int)(count * 4);
        var data = new float[bytes / 4];
        var buf = reader.ReadBytes(bytes);
        Buffer.BlockCopy(buf, 0, data, 0, bytes);
        return data;
    }

    public float[] Synthesize(string text, float speed = 1.0f)
    {
        var tokens = ToTokenIds(text);
        if (tokens.Length == 0) return [];
        var n = Math.Min(tokens.Length, 510);
        if (n != tokens.Length) tokens = tokens[..n];

        var input = new long[n + 2];
        input[0] = 0;
        input[^1] = 0;
        for (var i = 0; i < n; i++) input[i + 1] = tokens[i];

        var voice = _voices.TryGetValue(_voiceName, out var v) ? v : _voices.Values.FirstOrDefault() ?? Array.Empty<float>();
        var styleRow = n - 1;
        var style = new float[256];
        if (voice.Length > 0)
        {
            var rows = voice.Length / 256;
            var row = Math.Min(styleRow, rows - 1);
            Array.Copy(voice, row * 256, style, 0, 256);
        }

        var idsTensor = new DenseTensor<long>(input, new[] { 1, input.Length });
        var styleTensor = new DenseTensor<float>(style, new[] { 1, 256 });
        var speedTensor = new DenseTensor<float>(new[] { speed }, new[] { 1 });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", idsTensor),
            NamedOnnxValue.CreateFromTensor("style", styleTensor),
            NamedOnnxValue.CreateFromTensor("speed", speedTensor),
        };
        using var results = _session.Run(inputs);
        return results[0].AsTensor<float>().ToArray();
    }

    private long[] ToTokenIds(string text)
    {
        lock (_tokenCacheLock)
        {
            if (_tokenCache.TryGetValue(text, out var cached)) return cached;
        }
        var phonemes = G2p(text);
        var ids = new List<long>();
        foreach (var p in phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (_vocab.TryGetValue(p, out var id))
                ids.Add(id);
        var arr = ids.ToArray();
        lock (_tokenCacheLock)
        {
            _tokenCache[text] = arr;
        }
        return arr;
    }

    private string G2p(string text)
    {
        var ipa = EspeakIpa(text);
        if (string.IsNullOrEmpty(ipa)) return "";
        var sb = new StringBuilder();
        foreach (var ch in ipa)
        {
            if (ch == ' ') { sb.Append(' '); continue; }
            if (_vocab.ContainsKey(ch.ToString())) sb.Append(ch).Append(' ');
        }
        return sb.ToString().Trim();
    }

    private static string EspeakIpa(string text)
    {
        try
        {
            var exe = EspeakPath();
            if (string.IsNullOrEmpty(exe)) return "";
            var psi = new System.Diagnostics.ProcessStartInfo(exe, $"-q --ipa -v en-us \"{text.Replace("\"", "'")}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return "";
            var stdout = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            return stdout;
        }
        catch
        {
            return "";
        }
    }

    private static string EspeakPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "espeak-ng.exe"),
            @"C:\Program Files\eSpeak NG\espeak-ng.exe",
        };
        foreach (var c in candidates)
            try { if (File.Exists(c)) return c; } catch { }
        return "";
    }

    public void Dispose() => _session?.Dispose();
}