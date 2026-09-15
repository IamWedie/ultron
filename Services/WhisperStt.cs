using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Ultron.Services;

public sealed class WhisperStt : IDisposable
{
    private const int SampleRate = 16000;
    private const int Nfft = 400;
    private const int Nmels = 80;
    private const int HopLength = 160;
    private const int MaxFrames = 3000;
    private const int MaxTokens = 448;
    private const int EosId = 50256;

    private InferenceSession _encSess = null!;
    private InferenceSession _decSess = null!;
    private Dictionary<int, string> _idToToken = null!;
    private Dictionary<char, byte> _byteDecoder = null!;
    private static readonly long[] PrefixTokenIds = [50257, 50358, 50362];
    private static readonly bool[] UseCacheBranch = [false];
    private static readonly int[] Dim1 = [1];

    private static readonly float[,] MelBasis = BuildMelFilters();

    // Precomputed once (used by every ComputeMel call): Hamming window + FFT twiddles.
    private static readonly float[] Window = MakeWindow();
    private static readonly double[] TwReal; // twiddle cosines, Nfft * (Nfft/2+1)
    private static readonly double[] TwImag; // twiddle sines

    static WhisperStt()
    {
        var half = Nfft / 2 + 1;
        TwReal = new double[Nfft * half];
        TwImag = new double[Nfft * half];
        var idx = 0;
        for (var k = 0; k < half; k++)
            for (var n = 0; n < Nfft; n++)
            {
                var ang = -2.0 * Math.PI * k * n / Nfft;
                TwReal[idx] = Math.Cos(ang);
                TwImag[idx] = Math.Sin(ang);
                idx++;
            }
    }

    private static float[] MakeWindow()
    {
        var w = new float[Nfft];
        for (var i = 0; i < Nfft; i++)
            w[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / Nfft));
        return w;
    }

    public void Load(string modelDir)
    {
        var encPath = Path.Combine(modelDir, "whisper-encoder.onnx");
        var decPath = Path.Combine(modelDir, "whisper-decoder.onnx");
        var tokPath = Path.Combine(modelDir, "whisper-tokenizer.json");
        var opts = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 4 };
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        _encSess = new InferenceSession(encPath, opts);
        _decSess = new InferenceSession(decPath, opts);
        LoadTokenizer(tokPath);
    }

    public async Task<string> TranscribeAsync(float[] pcm16k)
    {
        if (pcm16k.Length == 0) return "";
        var prepared = NormalizeAudio(pcm16k);
        if (prepared.Length < 480) return "";
        var mel = ComputeMel(prepared);
        var inputTensor = new DenseTensor<float>(new[] { 1, Nmels, MaxFrames });
        for (var i = 0; i < Nmels; i++)
            for (var j = 0; j < MaxFrames; j++)
                inputTensor[0, i, j] = mel[i * MaxFrames + j];

        var encInput = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_features", inputTensor)
        };
        using var encOut = await Task.Run(() => _encSess.Run(encInput));
        var encHidden = encOut[0].AsTensor<float>().ToArray();

        var inputIds = new List<long>();
        inputIds.AddRange(PrefixTokenIds);
        var maxDecLen = Math.Min(MaxTokens, 448);

        for (var step = 0; step < maxDecLen; step++)
        {
            var idsTensor = new DenseTensor<long>(new[] { 1, inputIds.Count });
            for (var i = 0; i < inputIds.Count; i++)
                idsTensor[0, i] = inputIds[i];

            var encStatesTensor = new DenseTensor<float>(encHidden, new[] { 1, 1500, 384 });

            var decInput = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", idsTensor),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encStatesTensor),
                NamedOnnxValue.CreateFromTensor("use_cache_branch", new DenseTensor<bool>(UseCacheBranch, Dim1)),
            };

            using var decOut = await Task.Run(() => _decSess.Run(decInput));
            var logits = decOut[0].AsTensor<float>();
            var vocabSize = (int)(logits.Length / inputIds.Count);
            var lastIdx = inputIds.Count - 1;

            // Argmax over the last token row without materializing ToArray: the
            // DenseTensor buffer is row-major [1, seq, vocab] and is only read.
            var bestId = 0;
            var bestScore = float.MinValue;
            if (logits is DenseTensor<float> dense)
            {
                var span = dense.Buffer.Span;
                var rowStart = lastIdx * vocabSize;
                for (var v = 0; v < vocabSize && v + rowStart < span.Length; v++)
                {
                    var score = span[rowStart + v];
                    if (score > bestScore) { bestScore = score; bestId = v; }
                }
            }
            else
            {
                var flat = logits.ToArray();
                for (var v = 0; v < vocabSize; v++)
                {
                    var score = flat[lastIdx * vocabSize + v];
                    if (score > bestScore) { bestScore = score; bestId = v; }
                }
            }
            if (bestId == EosId) break;
            if (bestId < 50257) inputIds.Add(bestId);
            else break;
        }

        return DecodeIds(inputIds);
    }

    private static float[] NormalizeAudio(float[] pcm)
    {
        var start = 0;
        var end = pcm.Length;
        var voiced = new bool[pcm.Length];
        long absSum = 0;
        for (var i = 0; i < pcm.Length; i++)
        {
            var v = Math.Abs(pcm[i]);
            voiced[i] = v > 0.008f;
            absSum += (long)(v * 10000);
        }
        var meanAbs = absSum / (double)pcm.Length / 10000.0;
        if (meanAbs < 0.001) return Array.Empty<float>();
        if (meanAbs < 0.02)
        {
            var g = 0.02f / (float)meanAbs;
            for (var i = 0; i < pcm.Length; i++) pcm[i] = Math.Clamp(pcm[i] * g, -1f, 1f);
        }
        for (; start < end && !voiced[start]; start++) { }
        for (; end > start && !voiced[end - 1]; end--) { }
        if (end - start < 480) return Array.Empty<float>();
        return pcm[start..end];
    }

    private float[] ComputeMel(float[] pcm)
    {
        var padded = new float[MaxFrames * HopLength];
        Array.Copy(pcm, padded, Math.Min(pcm.Length, padded.Length));
        var nFftHalf = Nfft / 2 + 1;

        var magnitudes = new double[MaxFrames, nFftHalf];

        // Thread-local scratch buffers: reused across frames, zero per-frame allocs.
        Parallel.For(
            0, MaxFrames,
            static () => new Buffers(),
            (frame, _state, bufs) =>
            {
                var offset = frame * HopLength;
                for (var i = 0; i < Nfft; i++)
                {
                    var tIdx = offset + i - Nfft / 2;
                    bufs.Re[i] = (tIdx >= 0 && tIdx < padded.Length ? padded[tIdx] : 0) * Window[i];
                    bufs.Im[i] = 0;
                }
                var half = nFftHalf;
                var tIdx2 = 0;
                for (var k = 0; k < half; k++)
                {
                    var cr = 0.0;
                    var ci = 0.0;
                    for (var t = 0; t < Nfft; t++)
                    {
                        cr += bufs.Re[t] * TwReal[tIdx2] + bufs.Im[t] * TwImag[tIdx2];
                        ci += bufs.Im[t] * TwReal[tIdx2] - bufs.Re[t] * TwImag[tIdx2];
                        tIdx2++;
                    }
                    bufs.TmpRe[k] = cr;
                    bufs.TmpIm[k] = ci;
                }
                for (var k = 0; k < half; k++)
                    magnitudes[frame, k] = bufs.TmpRe[k] * bufs.TmpRe[k] + bufs.TmpIm[k] * bufs.TmpIm[k];
                return bufs;
            },
            _ => { });

        var melSpec = new double[Nmels * MaxFrames];
        for (var m = 0; m < Nmels; m++)
            for (var j = 0; j < MaxFrames; j++)
            {
                var energy = 0.0;
                for (var k = 0; k <= Nfft / 2; k++)
                    energy += magnitudes[j, k] * MelBasis[m, k];
                melSpec[m * MaxFrames + j] = Math.Max(energy, 1e-10);
            }

        var globalMax = double.MinValue;
        for (var i = 0; i < melSpec.Length; i++)
            if (melSpec[i] > globalMax) globalMax = melSpec[i];
        var floor = Math.Log10(globalMax) - 8.0;

        var mel = new float[Nmels * MaxFrames];
        for (var i = 0; i < melSpec.Length; i++)
        {
            var logSpec = Math.Log10(melSpec[i]);
            if (logSpec < floor) logSpec = floor;
            mel[i] = (float)((logSpec + 4.0) / 4.0);
        }
        return mel;
    }

    private sealed class Buffers
    {
        public readonly double[] Re = new double[Nfft];
        public readonly double[] Im = new double[Nfft];
        public readonly double[] TmpRe = new double[Nfft / 2 + 1];
        public readonly double[] TmpIm = new double[Nfft / 2 + 1];
    }

    private static float[,] BuildMelFilters()
    {
        var nfftHalf = Nfft / 2 + 1;
        var f = new float[Nmels, nfftHalf];

        var melLo = HzToMelSlaney(0.0);
        var melHi = HzToMelSlaney(8000.0);
        var centerHz = new double[Nmels + 2];
        for (var m = 0; m < Nmels + 2; m++)
            centerHz[m] = MelToHzSlaney(melLo + (melHi - melLo) * m / (Nmels + 1));

        for (var m = 0; m < Nmels; m++)
        {
            var i0 = (int)Math.Round(centerHz[m] * Nfft / SampleRate);
            var i1 = (int)Math.Round(centerHz[m + 1] * Nfft / SampleRate);
            var i2 = (int)Math.Round(centerHz[m + 2] * Nfft / SampleRate);
            i0 = Math.Max(0, i0);
            i2 = Math.Min(nfftHalf - 1, i2);
            for (var k = i0; k <= i2; k++)
            {
                if (k <= i1 && i1 > i0) f[m, k] = (float)(k - i0) / (i1 - i0);
                else if (k > i1 && i2 > i1) f[m, k] = (float)(i2 - k) / (i2 - i1);
            }
        }
        return f;
    }

    private static double HzToMelSlaney(double hz)
    {
        var fMin = 0.0;
        var fSp = 200.0 / 3;
        var mels = (hz - fMin) / fSp;
        var minLogHz = 1000.0;
        var minLogMel = (minLogHz - fMin) / fSp;
        var logstep = Math.Log(6.4) / 27.0;
        if (hz >= minLogHz)
            mels = minLogMel + Math.Log(hz / minLogHz) / logstep;
        return mels;
    }

    private static double MelToHzSlaney(double mel)
    {
        var fMin = 0.0;
        var fSp = 200.0 / 3;
        var minLogHz = 1000.0;
        var minLogMel = (minLogHz - fMin) / fSp;
        var logstep = Math.Log(6.4) / 27.0;
        double hz;
        if (mel >= minLogMel) hz = minLogHz * Math.Exp(logstep * (mel - minLogMel));
        else hz = fMin + fSp * mel;
        return hz;
    }

    private void LoadTokenizer(string path)
    {
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var vocab = new Dictionary<string, int>();
        if (root.TryGetProperty("model", out var model) && model.TryGetProperty("vocab", out var v))
            foreach (var kv in v.EnumerateObject())
                vocab[kv.Name] = kv.Value.GetInt32();

        if (root.TryGetProperty("added_tokens", out var added))
            foreach (var tok in added.EnumerateArray())
            {
                var id = tok.GetProperty("id").GetInt32();
                var content = tok.GetProperty("content").GetString() ?? "";
                vocab[content] = id;
            }

        _idToToken = new Dictionary<int, string>();
        foreach (var kv in vocab)
            _idToToken[kv.Value] = kv.Key;

        var bs = new List<int>();
        for (var b = 33; b <= 126; b++) bs.Add(b);
        for (var b = 161; b <= 172; b++) bs.Add(b);
        for (var b = 174; b <= 255; b++) bs.Add(b);
        var cs = new List<int>(bs);
        var n = 0;
        for (var b = 0; b < 256; b++)
        {
            if (!bs.Contains(b))
            {
                bs.Add(b);
                cs.Add(256 + n);
                n++;
            }
        }
        _byteDecoder = new Dictionary<char, byte>();
        for (var i = 0; i < bs.Count; i++)
            _byteDecoder[(char)cs[i]] = (byte)bs[i];
    }

    private string DecodeIds(List<long> ids)
    {
        var sb = new StringBuilder();
        foreach (var id in ids)
        {
            if (id >= EosId) continue;
            if (!_idToToken.TryGetValue((int)id, out var token)) continue;
            foreach (var ch in token)
            {
                if (_byteDecoder.TryGetValue(ch, out var b)) sb.Append((char)b);
                else sb.Append(ch);
            }
        }
        var text = sb.ToString();
        if (text.StartsWith(' ')) text = text[1..];
        return text.Trim();
    }

    public void Dispose()
    {
        _encSess?.Dispose();
        _decSess?.Dispose();
    }
}