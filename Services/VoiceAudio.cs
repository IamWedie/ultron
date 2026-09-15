namespace Ultron.Services;

public static class VoiceAudio
{
    public const int SampleRate = 16000;
    public const int FrameSize = 512;       // 32 ms
    public const int HopSize = 160;         // 10 ms hop
    public const int MelBands = 20;
    private const double MinHz = 300;
    private const double MaxHz = 7600;
    private const double FrameRmsGate = 0.004;

    private static readonly double[] Hamming = MakeHamming();
    private static readonly double[,] MelFilters = MakeFilters();

    private static double[] MakeHamming()
    {
        var w = new double[FrameSize];
        for (var i = 0; i < FrameSize; i++)
            w[i] = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (FrameSize - 1));
        return w;
    }

    private static double ToMel(double hz) => 2595.0 * Math.Log10(1 + hz / 700.0);
    private static double ToHz(double mel) => 700.0 * (Math.Pow(10, mel / 2595.0) - 1);
    private static int BinOf(double hz) => (int)Math.Round(hz * FrameSize / SampleRate);

    private static double[,] MakeFilters()
    {
        var n = FrameSize / 2 + 1;
        var melLo = ToMel(MinHz);
        var melHi = ToMel(MaxHz);
        var f = new double[MelBands, n];
        for (var m = 1; m <= MelBands; m++)
        {
            var m0 = melLo + (melHi - melLo) * (m - 1) / (MelBands + 1);
            var m1 = melLo + (melHi - melLo) * m / (MelBands + 1);
            var m2 = melLo + (melHi - melLo) * (m + 1) / (MelBands + 1);
            var i0 = BinOf(ToHz(m0));
            var i1 = BinOf(ToHz(m1));
            var i2 = BinOf(ToHz(m2));
            for (var k = Math.Max(1, i0); k <= Math.Min(i2, n - 1); k++)
                f[m - 1, k] = k <= i1 ? (double)(k - i0) / (i1 - i0) : (double)(i2 - k) / (i2 - i1);
        }
        return f;
    }

    public static void Fft(double[] re, double[] im, int n)
    {
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            var wre = Math.Cos(ang);
            var wim = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                var cr = 1.0;
                var ci = 0.0;
                for (var k = 0; k < len / 2; k++)
                {
                    var ur = re[i + k];
                    var ui = im[i + k];
                    var vr = re[i + k + len / 2] * cr - im[i + k + len / 2] * ci;
                    var vi = re[i + k + len / 2] * ci + im[i + k + len / 2] * cr;
                    re[i + k] = ur + vr;
                    im[i + k] = ui + vi;
                    re[i + k + len / 2] = ur - vr;
                    im[i + k + len / 2] = ui - vi;
                    (cr, ci) = (cr * wre - ci * wim, cr * wim + ci * wre);
                }
            }
        }
    }

    public static double[]? ExtractFeatures(ReadOnlySpan<float> pcm)
    {
        var frameCount = (pcm.Length - FrameSize) / HopSize + 1;
        if (frameCount < 3) return null;
        var feats = new double[MelBands];
        var weightSum = 0.0;
        var re = new double[FrameSize];
        var im = new double[FrameSize];
        var half = FrameSize / 2;
        for (var fr = 0; fr < frameCount; fr++)
        {
            var chunk = pcm.Slice(fr * HopSize, FrameSize);
            var rms = 0.0;
            for (var i = 0; i < FrameSize; i++)
            {
                var v = chunk[i];
                rms += v * v;
                re[i] = v * Hamming[i];
            }
            var frameRms = Math.Sqrt(rms / FrameSize);
            if (frameRms < FrameRmsGate) continue;
            var w = Math.Min(50.0, Math.Max(1.0, frameRms / FrameRmsGate));
            Array.Clear(im, 0, FrameSize);
            Fft(re, im, FrameSize);
            for (var m = 0; m < MelBands; m++)
            {
                var e = 0.0;
                for (var k = 0; k < half; k++)
                    e += (re[k] * re[k] + im[k] * im[k]) * MelFilters[m, k];
                feats[m] += Math.Log10(e + 1e-12) * w;
            }
            weightSum += w;
        }
        if (weightSum <= 0) return null;
        for (var m = 0; m < MelBands; m++) feats[m] /= weightSum;
        var norm = 0.0;
        for (var m = 0; m < MelBands; m++) norm += feats[m] * feats[m];
        norm = Math.Sqrt(norm);
        if (norm < 1e-9) return null;
        for (var m = 0; m < MelBands; m++) feats[m] /= norm;
        return feats;
    }

    public static double Cosine(double[] a, double[] b)
    {
        var dot = 0.0;
        for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }

    public static double BestMatchScore(ReadOnlySpan<float> pcm, IReadOnlyList<double[]> references)
    {
        if (references.Count == 0) return 0;
        var winMs = 900;                 // 0.9 s scoring window
        var hopMs = 250;                 // 0.25 s hop
        var win = SampleRate * winMs / 1000;
        var hop = SampleRate * hopMs / 1000;
        var best = 0.0;
        if (pcm.Length >= win)
        {
            for (var start = 0; start + win <= pcm.Length; start += hop)
            {
                var f = ExtractFeatures(pcm.Slice(start, win));
                if (f is null) continue;
                var s = 0.0;
                for (var i = 0; i < references.Count; i++)
                    s = Math.Max(s, Cosine(f, references[i]));
                best = Math.Max(best, s);
            }
        }
        else
        {
            var f = ExtractFeatures(pcm);
            if (f is not null)
                for (var i = 0; i < references.Count; i++)
                    best = Math.Max(best, Cosine(f, references[i]));
        }
        return best;
    }
}