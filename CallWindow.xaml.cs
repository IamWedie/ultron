// CallWindow.xaml.cs - standalone call window
// A phone-shaped surface for Telegram calls: caller name, state, call time and
// hangup. It lives in its own window so it stays small and stays on top of
// whatever the user is doing instead of covering the conversation.

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Ultron.Services;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Ultron;

public sealed partial class CallWindow : Window
{
    private readonly AppWindow _appWindow;
    private bool _closing;

    public event Action? EndCallRequested;
    public event Action? AcceptCallRequested;
    public event Action? MuteToggledRequested;

    /// <summary>Muted state as last reported by the toggle, so the button can
    /// show the *next* action rather than an unlabelled current state.</summary>
    public bool IsMuted { get; private set; }

    public CallWindow()
    {
        InitializeComponent();

        _appWindow = AppWindow;
        _appWindow.Title = "ULTRON Call";

        var presenter = OverlappedPresenter.Create();
        // Phone-sized, always-on-top so the call surface survives alt-tabbing.
        presenter.IsMaximizable = false;
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        _appWindow.SetPresenter(presenter);
        // 320x460 is a phone's proportions; the small minimum keeps the window
        // from being resized into an unreadable strip.
        _appWindow.Resize(new SizeInt32(320, 460));

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(CallTitleBar);
        var tb = _appWindow.TitleBar;
        tb.BackgroundColor = Color.FromArgb(0xFF, 0x23, 0x24, 0x2A);
        tb.InactiveBackgroundColor = Color.FromArgb(0xFF, 0x23, 0x24, 0x2A);
        tb.ButtonBackgroundColor = Color.FromArgb(0xFF, 0x23, 0x24, 0x2A);
        tb.ButtonInactiveBackgroundColor = Color.FromArgb(0xFF, 0x23, 0x24, 0x2A);
        tb.ButtonHoverBackgroundColor = Color.FromArgb(0xFF, 0x33, 0x34, 0x3A);
        tb.ButtonPressedBackgroundColor = Color.FromArgb(0xFF, 0xFF, 0x2E, 0x2E);

        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            NativeTheme.ApplyDarkCaption(hwnd);
        }
        catch (Exception ex)
        {
            AppLog.Write("CallWindow", $"Dark caption failed: {ex.Message}", AppLog.Level.Warn);
        }

        Closed += OnClosed;
        ApplyIdle();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_closing) return;
        _closing = true;
        // A window the user closed by hand should not leave a call up with no
        // controls to hang up on, so treat closing as ending the call.
        EndCallRequested?.Invoke();
    }

    /// <summary>Brings the window up without stealing focus from whatever the
    /// user is typing in. A ring is not a reason to yank the caret.</summary>
    public void ShowWithoutActivation()
    {
        _appWindow.Show();
        TryActivateWithoutStealingFocus();
    }

    private void TryActivateWithoutStealingFocus()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            CallWindowNative.ShowNoActivate(hwnd);
        }
        catch (Exception ex)
        {
            AppLog.Write("CallWindow", $"ShowWindow failed: {ex.Message}", AppLog.Level.Warn);
        }
    }

    public void SetTarget(string? name)
    {
        var shown = string.IsNullOrWhiteSpace(name) ? "Unknown caller" : name.Trim();
        CallTargetText.Text = shown;
        CallAvatarInitial.Text = shown.Length > 1 ? char.ToUpperInvariant(shown[0]).ToString() : "?";
    }

    public void SetDuration(TimeSpan elapsed)
    {
        var total = (int)elapsed.TotalSeconds;
        CallDurationText.Text = total >= 3600
            ? $"{(total / 3600):D2}:{(total % 3600 / 60):D2}:{(total % 60):D2}"
            : $"{total / 60:D2}:{total % 60:D2}";
    }

    /// <summary>Applies a call state. Unknown states render as the raw value
    /// rather than being swallowed, so a new backend state is visible in the UI
    /// instead of looking like nothing happened.</summary>
    public void ApplyState(string state, string message)
    {
        var key = state.ToLowerInvariant();
        var (label, colour, live) = key switch
        {
            "ringing" => ("RINGING", Microsoft.UI.Colors.Orange, false),
            "connecting" => ("CONNECTING", Microsoft.UI.Colors.Orange, false),
            "connected" => ("CONNECTED", Microsoft.UI.Colors.LimeGreen, true),
            "remote_speech" => ("SPEAKING", Microsoft.UI.Colors.Cyan, true),
            "local_speech" => ("YOU SPEAKING", Microsoft.UI.Colors.Cyan, true),
            "ended" => ("ENDED", Microsoft.UI.Colors.Gray, false),
            "error" => ("FAILED", Microsoft.UI.Colors.Red, false),
            "unavailable" => ("UNAVAILABLE", Microsoft.UI.Colors.Red, false),
            _ => (key.ToUpperInvariant(), Microsoft.UI.Colors.Gray, false),
        };

        CallStateLabel.Text = label;
        CallStateText.Text = message;
        CallStateDot.Fill = new SolidColorBrush(colour);
        // The avatar ring tracks the state colour at low alpha: present but not
        // competing with the state dot.
        CallAvatarRingBrush.Color = Color.FromArgb(0x4D, colour.R, colour.G, colour.B);

        // A finished call has nothing left to control, so the controls go away
        // rather than offering a hangup for a call that already hung up.
        var terminal = CallStateRules.IsTerminal(state);
        CallAcceptButton.Visibility = Visibility.Collapsed;
        CallEndButton.Visibility = terminal ? Visibility.Collapsed : Visibility.Visible;
        CallMuteButton.Visibility = terminal ? Visibility.Collapsed : Visibility.Visible;

        // Countdown runs only for a live call; ringing has no meaningful timer.
        SetCounting(live);

        if (terminal)
            ArmAutoClose();
    }

    private System.Threading.Timer? _closeTimer;

    /// <summary>Leaves the finished call on screen briefly, like a real phone
    /// shows the call summary, then closes itself.</summary>
    private void ArmAutoClose()
    {
        _counting = false;
        _closeTimer?.Dispose();
        _closeTimer = new System.Threading.Timer(
            _ => DispatcherQueue.TryEnqueue(Close), null, 6000, Timeout.Infinite);
    }

    private bool _counting;

    /// <summary>Starts or stops the displayed call clock. This window owns no
    /// timer of its own: MainWindow holds the elapsed value and pushes it here,
    /// so two tickers cannot race to write the same number.</summary>
    public void SetCounting(bool counting)
    {
        if (counting == _counting) return;
        _counting = counting;
        // A live call starts from zero; anything left over from the last call
        // would otherwise make a fresh call look like it had been running.
        if (counting)
        {
            _closeTimer?.Dispose();
            SetDuration(TimeSpan.Zero);
        }
    }

    public void ApplyIdle()
    {
        CallStateLabel.Text = "IDLE";
        CallStateText.Text = "No active call";
        SetTarget(null);
        SetDuration(TimeSpan.Zero);
        CallEndButton.Visibility = Visibility.Visible;
        CallMuteButton.Visibility = Visibility.Visible;
        CallAcceptButton.Visibility = Visibility.Collapsed;
        SetCounting(false);
    }

    public void SetMuted(bool muted)
    {
        IsMuted = muted;
        // E720 is the microphone. Muted swaps to E74F (the mute glyph) and a red
        // face, so the button reads as current state rather than only as the
        // action it will perform.
        CallMuteIcon.Glyph = muted ? "\uE74F" : "\uE720";
        CallMuteLabel.Text = muted ? "Unmute" : "Mute";
        CallMuteIcon.Foreground = muted
            ? new SolidColorBrush(Microsoft.UI.Colors.White)
            : new SolidColorBrush(Color.FromArgb(0xFF, 0xC9, 0xCE, 0xD6));
        CallMuteButton.Background = muted
            ? new SolidColorBrush(Microsoft.UI.Colors.Crimson)
            : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        ToolTipService.SetToolTip(CallMuteButton, muted ? "Unmute Microphone" : "Mute Microphone");
    }

    private void CallEndButton_Click(object sender, RoutedEventArgs e) => EndCallRequested?.Invoke();

    private void CallAcceptButton_Click(object sender, RoutedEventArgs e) => AcceptCallRequested?.Invoke();

    private void CallMuteButton_Click(object sender, RoutedEventArgs e) => MuteToggledRequested?.Invoke();
}

internal static class CallWindowNative
{
    private const int SW_SHOWNOACTIVATE = 4;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    internal static void ShowNoActivate(IntPtr hwnd) => ShowWindow(hwnd, SW_SHOWNOACTIVATE);
}
