namespace Ultron.Services;

public enum AssistantState
{
    Sleep,
    WakeListening,
    Engaged,
    Rest,
}

public class StateTransition
{
    public AssistantState From { get; init; }
    public AssistantState To { get; init; }
    public string Reason { get; init; } = "";
}

public class AssistantStateMachine : IDisposable
{
    private readonly AppSettings _settings;
    private DateTime _engagedSince;
    private DateTime _lastVoiceActivity;
    private System.Threading.Timer? _inactivityTimer;
    private System.Threading.Timer? _wakeTimer;

    public AssistantState Current { get; private set; } = AssistantState.Sleep;

    public event Action<StateTransition>? StateChanged;
    public event Action? MicRequested;
    public event Action? MicSilenced;
    public event Action? ListenStarted;
    public event Action? ListenStopped;

    public DateTime EngagedSince => _engagedSince;

    public AssistantStateMachine(AppSettings settings)
    {
        _settings = settings;
    }

    public void WakeDetected()
    {
        if (Current == AssistantState.Rest)
        {
            Transition(AssistantState.WakeListening, "rest interrupted by wake word");
            StartWakeTimeout();
            return;
        }
        if (Current != AssistantState.Sleep) return;
        Transition(AssistantState.WakeListening, "wake word detected");
        MicRequested?.Invoke();
        StartWakeTimeout();
    }

    public void VoiceIdPassed()
    {
        if (Current != AssistantState.WakeListening) return;
        _engagedSince = DateTime.UtcNow;
        _lastVoiceActivity = DateTime.UtcNow;
        Transition(AssistantState.Engaged, "voice ID verified");
        _inactivityTimer?.Dispose();
        _inactivityTimer = new System.Threading.Timer(OnInactivity, null,
            TimeSpan.FromSeconds(_settings.EngagedTimeoutSeconds), Timeout.InfiniteTimeSpan);
        ListenStarted?.Invoke();
    }

    public void VoiceIdFailed()
    {
        if (Current != AssistantState.WakeListening) return;
        Transition(AssistantState.Sleep, "voice ID rejected — returning to sleep");
        _wakeTimer?.Dispose();
        MicSilenced?.Invoke();
    }

    public void CommandStarted()
    {
        if (Current != AssistantState.Engaged) return;
        _lastVoiceActivity = DateTime.UtcNow;
        ResetInactivityTimer();
    }

    public void CommandFinished()
    {
        if (Current != AssistantState.Engaged) return;
        _lastVoiceActivity = DateTime.UtcNow;
        ResetInactivityTimer();
    }

    public void RestCue()
    {
        if (Current != AssistantState.Engaged) return;
        Transition(AssistantState.Rest, "user issued rest cue");
        ListenStopped?.Invoke();
        MicSilenced?.Invoke();
        _inactivityTimer?.Dispose();
    }

    public void ForceSleep()
    {
        if (Current == AssistantState.Sleep) return;
        Transition(AssistantState.Sleep, "forced sleep");
        _inactivityTimer?.Dispose();
        _wakeTimer?.Dispose();
        ListenStopped?.Invoke();
        MicSilenced?.Invoke();
    }

    public void ResetForNewSession()
    {
        ForceSleep();
    }

    public void EngageFromText()
    {
        _lastVoiceActivity = DateTime.UtcNow;
        if (Current == AssistantState.Engaged)
        {
            ResetInactivityTimer();
            return;
        }
        _wakeTimer?.Dispose();
        _engagedSince = DateTime.UtcNow;
        if (Current == AssistantState.Sleep || Current == AssistantState.Rest)
        {
            Transition(AssistantState.WakeListening, "text input received while idle");
        }
        Transition(AssistantState.Engaged, "text input verified — session active");
        _inactivityTimer?.Dispose();
        _inactivityTimer = new System.Threading.Timer(OnInactivity, null,
            TimeSpan.FromSeconds(_settings.EngagedTimeoutSeconds), Timeout.InfiniteTimeSpan);
    }

    private void OnInactivity(object? state)
    {
        var idle = (DateTime.UtcNow - _lastVoiceActivity).TotalSeconds;
        if (Current == AssistantState.Engaged && idle >= _settings.EngagedTimeoutSeconds)
        {
            Transition(AssistantState.Sleep, $"inactivity timeout ({idle:F0}s)");
            ListenStopped?.Invoke();
            MicSilenced?.Invoke();
        }
    }

    private void OnWakeTimeout(object? state)
    {
        if (Current == AssistantState.WakeListening)
        {
            Transition(AssistantState.Sleep, "wake listening timed out — no voice ID match");
            MicSilenced?.Invoke();
        }
    }

    private void StartWakeTimeout()
    {
        _wakeTimer?.Dispose();
        _wakeTimer = new System.Threading.Timer(OnWakeTimeout, null,
            TimeSpan.FromSeconds(_settings.WakeListeningTimeoutSeconds), Timeout.InfiniteTimeSpan);
    }

    private void ResetInactivityTimer()
    {
        _inactivityTimer?.Dispose();
        _inactivityTimer = new System.Threading.Timer(OnInactivity, null,
            TimeSpan.FromSeconds(_settings.EngagedTimeoutSeconds), Timeout.InfiniteTimeSpan);
    }

    private void Transition(AssistantState to, string reason)
    {
        var from = Current;
        if (from == to) return;
        Current = to;
        StateChanged?.Invoke(new StateTransition { From = from, To = to, Reason = reason });
    }

    public void Dispose()
    {
        _inactivityTimer?.Dispose();
        _wakeTimer?.Dispose();
        _inactivityTimer = null;
        _wakeTimer = null;
        GC.SuppressFinalize(this);
    }
}