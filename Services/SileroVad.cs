using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Ultron.Services;

public sealed class SileroVad : IDisposable
{
    private readonly InferenceSession _session;
    private readonly DenseTensor<float> _state = new(new[] { 2, 1, 128 });
    private readonly long[] _srData = { 16000 };
    private readonly DenseTensor<long> _sr = new(new long[] { 16000 }, new[] { 1 });
    private readonly float[] _input = new float[64 + 512];
    private readonly float[] _context = new float[64];
    private int _contextLen;
    private bool _resetPending = true;

    public float Threshold { get; set; } = 0.5f;
    public int MinSilenceSamples { get; set; } = 1600;
    public int SpeechPadSamples { get; set; } = 480;

    public SileroVad(string modelPath)
    {
        var opts = new SessionOptions { IntraOpNumThreads = 2 };
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        _session = new InferenceSession(modelPath, opts);
    }

    public void Reset()
    {
        _resetPending = true;
        _state.Buffer.Span.Clear();
        Array.Clear(_context, 0, _context.Length);
        _contextLen = 0;
    }

    private void ResetState()
    {
        _state.Buffer.Span.Clear();
        _resetPending = false;
    }

    public float PredictFrame(ReadOnlySpan<float> chunk)
    {
        if (_resetPending) ResetState();

        // Reuse _input (no per-frame allocation): [context tail][512 audio][zero pad]
        var kept = Math.Min(_contextLen, 64);
        Array.Clear(_input, 0, 64 - kept);
        Array.Copy(_context, 0, _input, 64 - kept, kept);
        var n = Math.Min(512, chunk.Length);
        chunk[..n].CopyTo(_input.AsSpan(64));
        Array.Clear(_input, 64 + n, 512 - n);

        var inputTensor = new DenseTensor<float>(new Memory<float>(_input), new[] { 1, 576 });

        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("state", _state),
            NamedOnnxValue.CreateFromTensor("sr", _sr),
        });

        var probTensor = results[0].AsTensor<float>();
        var prob = probTensor is DenseTensor<float>
            ? probTensor[0]
            : probTensor.ToArray()[0];

        var target = _state.Buffer.Span;
        if (results[1].AsTensor<float>() is DenseTensor<float> dt)
            dt.Buffer.Span.CopyTo(target);
        else
            results[1].AsTensor<float>().ToArray().CopyTo(target);

        Array.Copy(_input, 512, _context, 0, 64);
        _contextLen = 64;
        return prob;
    }

    public void Dispose() => _session?.Dispose();
}