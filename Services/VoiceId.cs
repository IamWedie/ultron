using System.Text.Json;

namespace Ultron.Services;

public sealed class VoiceId
{
    private readonly List<double[]> _wake = new();
    private readonly List<double[]> _owner = new();
    private readonly Queue<float> _ring = new();
    private DateTime _lastScan = DateTime.MinValue;
    private DateTime _lastFire = DateTime.MinValue;
    private int _hitStreak;

    public double WakeThreshold { get; set; } = 0.78;
    public double OwnerThreshold { get; set; } = 0.86;
    public bool WakeEnrolled => _wake.Count > 0;
    public bool OwnerEnrolled => _owner.Count > 0;
    public int WakeEnrolledCount() => _wake.Count;
    public int OwnerEnrolledCount() => _owner.Count;

    public event Action? WakeSpotted;

    private const int MonitorWindow = VoiceAudio.SampleRate;              // 1 s
    private const int RingCapacity = (int)(VoiceAudio.SampleRate * 1.4);  // 1.4 s
    private static readonly TimeSpan FireCooldown = TimeSpan.FromSeconds(3);

    public double AddWakeSample(float[] pcm)
    {
        var f = VoiceAudio.ExtractFeatures(pcm);
        if (f is null) return -1;
        var score = _wake.Count > 0 ? _wake.Max(w => VoiceAudio.Cosine(f, w)) : 1.0;
        _wake.Add(f);
        return score;
    }

    public double AddOwnerSample(float[] pcm)
    {
        var f = VoiceAudio.ExtractFeatures(pcm);
        if (f is null) return -1;
        var score = _owner.Count > 0 ? _owner.Max(o => VoiceAudio.Cosine(f, o)) : 1.0;
        _owner.Add(f);
        return score;
    }

    public double ScoreWorstMatch()
    {
        if (_wake.Count < 2) return 0;
        double worst = 1;
        for (var i = 0; i < _wake.Count; i++)
        for (var j = i + 1; j < _wake.Count; j++)
            worst = Math.Min(worst, VoiceAudio.Cosine(_wake[i], _wake[j]));
        return worst;
    }

    public void Feed(ReadOnlySpan<float> pcm)
    {
        lock (_ring)
        {
            foreach (var v in pcm)
            {
                _ring.Enqueue(v);
                if (_ring.Count > RingCapacity) _ring.Dequeue();
            }
            if (_ring.Count < MonitorWindow) return;
            var now = DateTime.UtcNow;
            if (now - _lastScan < TimeSpan.FromMilliseconds(80)) return;
            _lastScan = now;
            if (now - _lastFire < FireCooldown) return;

            var buf = new float[MonitorWindow];
            var arr = _ring.ToArray();
            Array.Copy(arr, arr.Length - MonitorWindow, buf, 0, MonitorWindow);
            var f = VoiceAudio.ExtractFeatures(buf);
            if (f is null) { _hitStreak = 0; return; }
            var score = _wake.Max(w => VoiceAudio.Cosine(f, w));
            if (score >= WakeThreshold)
            {
                _hitStreak++;
                if (_hitStreak >= 2)
                {
                    _hitStreak = 0;
                    _lastFire = now;
                    WakeSpotted?.Invoke();
                }
            }
            else
            {
                _hitStreak = 0;
            }
        }
    }

    public bool Verify(float[] pcm)
    {
        if (_owner.Count == 0) return false;
        return BestOwnerScore(pcm) >= OwnerThreshold;
    }

    public double BestOwnerScore(float[] pcm)
    {
        if (_owner.Count == 0) return 0;
        return VoiceAudio.BestMatchScore(pcm, _owner);
    }

    public double LastOwnerScore(float[] pcm)
    {
        return BestOwnerScore(pcm);
    }

    public string ToProfile()
    {
        return JsonSerializer.Serialize(new ProfileData
        {
            Wake = _wake.Select(v => v.ToArray()).ToArray(),
            Owner = _owner.Select(v => v.ToArray()).ToArray(),
        });
    }

    public void LoadProfile(string json)
    {
        _wake.Clear();
        _owner.Clear();
        try
        {
            var d = JsonSerializer.Deserialize<ProfileData>(json);
            if (d is null) return;
            _wake.AddRange(d.Wake ?? []);
            _owner.AddRange(d.Owner ?? []);
        }
        catch { }
    }

    private sealed class ProfileData
    {
        public double[][]? Wake { get; set; }
        public double[][]? Owner { get; set; }
    }
}