using System.Buffers;
using NAudio.Wave;

namespace Ultron.Services;

public sealed class AudioCapture : IDisposable
{
    public delegate void SamplesHandler(ReadOnlySpan<float> pcm);

    private readonly WaveIn _capture;

    public event SamplesHandler? Samples;
    public event Action<float>? Level;

    public bool IsActive { get; private set; }
    public int SampleRate => 16000;

    private double _recentPeak;
    private const float TargetPeak = 0.30f;
    private const float MinGain = 1.0f;
    private const float MaxGain = 12.0f;

    private void ApplyAgc(Span<float> floats)
    {
        var peak = 0.0d;
        for (var i = 0; i < floats.Length; i++)
        {
            var v = Math.Abs(floats[i]);
            if (v > peak) peak = v;
        }
        var sm = 0.9 * _recentPeak + 0.1 * peak;
        _recentPeak = sm;
        if (sm < 1e-4) return;
        var gain = (float)Math.Clamp(TargetPeak / sm, MinGain, MaxGain);
        for (var i = 0; i < floats.Length; i++)
        {
            var v = floats[i] * gain;
            if (v > 1f) v = 1f;
            else if (v < -1f) v = -1f;
            floats[i] = v;
        }
    }

    public AudioCapture()
    {
        _capture = new WaveIn
        {
            DeviceNumber = -1,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
        };
        _capture.DataAvailable += OnData;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var n = e.BytesRecorded / 2;
        if (n <= 0) return;
        var floats = ArrayPool<float>.Shared.Rent(n);
        try
        {
            for (var i = 0; i < n; i++)
            {
                var v = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8));
                floats[i] = v / 32768f;
            }
            var slice = floats.AsSpan(0, n);
            ApplyAgc(slice);
            var sum = 0.0d;
            for (var i = 0; i < n; i++) sum += slice[i] * (double)slice[i];
            // Consumers (voice-id ring, verify sink, VAD loop, STT queue) copy
            // synchronously, so the rented buffer is safe to return afterwards —
            // the speech path now allocates nothing per audio chunk.
            Samples?.Invoke(slice);
            Level?.Invoke((float)Math.Sqrt(sum / n));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floats);
        }
    }

    public string Start()
    {
        if (IsActive) return "";
        try
        {
            _capture.StartRecording();
            IsActive = true;
            return "";
        }
        catch (Exception ex)
        {
            return "No microphone available: " + ex.Message;
        }
    }

    public void Stop()
    {
        if (!IsActive) return;
        try { _capture.StopRecording(); } catch { }
        IsActive = false;
    }

    public void Dispose()
    {
        Stop();
        _capture.DataAvailable -= OnData;
        try { _capture.Dispose(); } catch { }
    }
}
