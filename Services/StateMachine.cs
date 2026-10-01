namespace Ultron.Services;

public enum AssistantState
{
    Sleep,
    Engaged,
    Rest,
}

public class StateTransition
{
    public AssistantState From { get; init; }
    public AssistantState To { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// Tracks whether a conversation is in progress, for the HUD orb and the
/// inactivity timeout. It is driven by text and by Gemini transcripts.
///
/// This used to also model the local wake-word handshake: a WakeListening state
/// between "wake word heard" and "owner voice verified". That whole path is
/// gone, because local speaker verification was deleted along with the Whisper
/// STT pipeline. The wake phrase is now enforced by the Gemini system prompt, so
/// there is no on-device handshake for this machine to observe. Sleep and
/// mic-open/closed are owned by the backend's gate, not here.
/// </summary>
public class AssistantStateMachine : IDisposable
{
    private readonly AppSettings _settings;
    private DateTime _engagedSince;
    private DateTime _lastVoiceActivity;
    private System.Threading.Timer? _inactivityTimer;

    public AssistantState Current { get; private set; } = AssistantState.Sleep;

    public event Action<StateTransition>? StateChanged;
    public event Action? ListenStarted;
    public event Action? ListenStopped;

    public DateTime EngagedSince => _engagedSince;

    public AssistantStateMachine(AppSettings settings)
    {
        _settings = settings;
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
        _inactivityTimer?.Dispose();
    }

    public void ForceSleep()
    {
        if (Current == AssistantState.Sleep) return;
        Transition(AssistantState.Sleep, "forced sleep");
        _inactivityTimer?.Dispose();
        ListenStopped?.Invoke();
    }

    public void ResetForNewSession()
    {
        ForceSleep();
    }

    /// <summary>Mark the session as live. Text no longer passes through a local
    /// verification step, so this engages directly.</summary>
    public void EngageFromText()
    {
        _lastVoiceActivity = DateTime.UtcNow;
        if (Current == AssistantState.Engaged)
        {
            ResetInactivityTimer();
            return;
        }
        _engagedSince = DateTime.UtcNow;
        Transition(AssistantState.Engaged, "text input received — session active");
        ListenStarted?.Invoke();
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
        }
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
        _inactivityTimer = null;
        GC.SuppressFinalize(this);
    }
}