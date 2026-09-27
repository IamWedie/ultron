// MainWindow.xaml.cs - composition root: fields, constructor, window lifecycle
// Part of MainWindow. Fields and composition root live in MainWindow.xaml.cs.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Ultron.Services;
using Ultron.Services.Apps;
using Ultron.Services.Processes;
using Ultron.Services.Tools.Apps;
using Windows.System;
using Windows.UI;
using Microsoft.UI;
using Xaml = Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Buffers;
using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Ultron;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "MainWindow is the composition root; it disposes owned services once in its Closed handler.")]
public sealed partial class MainWindow : Window, IApprovalService, ILaunchedWindowActivator
{
    internal static void Dbg(string msg)
    {
        AppLog.Write("Main", $"[{Environment.CurrentManagedThreadId}] {msg}");
    }
    private readonly AppWindow _appWindow;
    private DateTime _lastInteraction = DateTime.UtcNow;
    private readonly AppSettings _settings;
    internal readonly MemoryStore _memory;
    internal readonly Brain _brain;
    internal readonly Outreach _outreach;
    internal readonly AssistantStateMachine _sm;
    internal readonly SystemTelemetry _telemetry = new();
    internal AudioCapture? _audio;
    internal readonly VoiceId _voiceId = new();
    private readonly object _verifyLock = new();
    private readonly DispatcherTimer _verifyWatch = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly System.Collections.Generic.Queue<double> _spark = new();
    private List<float>? _verifyBuffer;
    private List<float>? _captureSink;
    private DateTime _verifyStart;
    private int _silentTicks;

    internal readonly ModelRepo _repo;
    internal readonly GeminiBackend _gemini;
    internal TextBlock? _tgStatusText;
    internal StackPanel? _tgCodePanel;
    internal TextBox? _tgCodeBox;
    internal volatile bool _geminiMode;
    internal bool _awakeInGemini = true;
    internal bool _wakeGateOn;
    private IntPtr _selfHwnd;
    internal IntPtr _lastTargetWindow = IntPtr.Zero;
    private WhisperStt? _whisper;
    internal SileroVad? _vad;
    internal readonly List<float> _vadBuf = new();
    internal readonly List<float> _speechSeg = new();
    internal bool _vadSpeaking;
    internal int _vadSilenceCount;
    internal const int VadSilenceFrames = 22;
    internal bool _modelsLoaded;
    internal readonly ConcurrentQueue<float[]> _sttQueue = new();
    internal bool _sttPumping;

    private TrayIcon? _tray;
    internal HotkeyService? _hotkeys;
    internal NotificationService? _notify;
    private volatile bool _micMuted;
    internal DispatcherTimer? _heartbeatTimer;
    internal DispatcherTimer? _callTimer;
    internal TimeSpan _callDuration;
    private const int HotkeyPtt = 1, HotkeyMute = 2, HotkeyWake = 3;

    // Centralized tool registry with authorization metadata
    private readonly ToolRouter _toolRouter;
    private readonly UndoLedger _undo = new();
    private readonly CommandRunner _commandRunner = new();

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        _micMuted = _settings.MicMuted;
        UltronOptions.Set(_settings);
        AppLog.SetVerbosity(_settings.LogLevel);
        _memory = new MemoryStore();
        _brain = new Brain(_settings, _memory);
        _outreach = new Outreach(_settings);
        _outreach.RegisterSmsSender((number, text) => _brain.SendSms(number, text));
        _sm = new AssistantStateMachine(_settings);
        _gemini = new GeminiBackend();
        _gemini.TelegramOptions = new TelegramCallOptions(
            _settings.TelegramApiId, _settings.TelegramApiHash,
            _settings.TelegramPhone, _settings.TelegramCallEnabled,
            _settings.TelegramCallTarget);
        _toolRouter = BuildToolRouter();

        _gemini.StatusChanged += OnGeminiStatusChanged;
        _gemini.TranscriptReceived += OnGeminiTranscript;
        _gemini.ToolCallReceived += OnGeminiToolCall;
        _gemini.ComputerActRequested += OnComputerActRequested;
        _gemini.ErrorOccurred += OnGeminiError;
        _gemini.Disconnected += OnGeminiDisconnected;
        _gemini.ContactRequested += OnContactRequested;
        _gemini.TelegramStatusChanged += (phase, msg, avail) =>
        {
            _ = DispatcherQueue?.TryEnqueue(() =>
            {
                if (phase == "connected") HideTelegramCodePanel();
                if (_tgStatusText is not null) _tgStatusText.Text = $"[{phase.ToUpperInvariant()}]  {msg}";
                AddMessage("system", $"Telegram: {msg}");
            });
        };
        _gemini.TelegramCodeRequired += (phone, hint) =>
        {
            _ = DispatcherQueue?.TryEnqueue(() => PromptTelegramCode(phone, hint));
        };
        _gemini.TelegramCodeResult += (ok, msg) =>
        {
            _ = DispatcherQueue?.TryEnqueue(() =>
            {
                if (ok) HideTelegramCodePanel();
                AddMessage("system",
                    ok ? $"Telegram code accepted — {msg}" : $"Telegram login failed: {msg}");
            });
        };
        _gemini.TelegramTargetResolved += result =>
        {
            _ = DispatcherQueue?.TryEnqueue(() =>
            {
                if (result.Ok)
                {
                    lock (_settings)
                    {
                        _settings.TelegramCallTarget = result.Target;
                        _settings.TelegramCallUserId = result.UserId;
                        _settings.Save();
                    }
                }
                if (_tgStatusText is not null)
                    _tgStatusText.Text = result.Ok
                        ? $"Call target: {result.Target} [{result.UserId}] ({result.Name})"
                        : $"Target resolve failed: {result.Message}";
                AddMessage("system", result.Ok
                    ? $"Call target resolved: {result.Target} ({result.Name})"
                    : $"Telegram target resolve failed: {result.Message}");
            });
        };
        _gemini.TelegramCallStateChanged += (state, message) =>
        {
            _ = DispatcherQueue?.TryEnqueue(() =>
            {
                if (_tgStatusText is not null)
                    _tgStatusText.Text = $"[{state.ToUpperInvariant()}]  {message}";
                AddMessage("system", $"Telegram call: {message}");
                HandleCallStateChanged(state, message);
            });
        };
        _gemini.TelegramCallResult += (ok, message) =>
        {
            _ = DispatcherQueue?.TryEnqueue(() =>
            {
                AddMessage("system", ok
                    ? $"Telegram call: {message}"
                    : $"Telegram call failed: {message}");
            });
        };
        _gemini.TelegramCallFallbackSent += (source, target, reason, ok, message) =>
        {
            _ = DispatcherQueue?.TryEnqueue(() =>
            {
                if (ok)
                {
                    AddMessage("system", $"📩 Call fallback sent to {target} ({source}: {reason})");
                }
                else
                {
                    // The owner was not reached. Saying "sent" here would hide the
                    // one case the whole feature exists to prevent.
                    AddMessage("error",
                        $"⚠️ Call fallback FAILED - the owner was not reached "
                        + $"({source}: {reason}). {message}");
                }
            });
        };

        // Stop call timer on close
        Closed += (_, _) => _callTimer?.Stop();

        Closed += async (_, _) =>
        {
            Watchdog.WriteQuitFlag();
            _heartbeatTimer?.Stop();
            _hotkeys?.Dispose();
            _notify?.Dispose();
            _tray?.Dispose();
            await _gemini.StopAsync();
            _gemini.Dispose();
            _audio?.Dispose();
            _vad?.Dispose();
            _whisper?.Dispose();
            _memory?.Dispose();
            _sm?.Dispose();
            _brain.Dispose();
            _outreach?.Dispose();
        };
        _sm.StateChanged += OnStateChanged;
        _sm.MicRequested += () => SetMicVisual(true);
        _sm.MicSilenced += () => SetMicVisual(false);
        _sm.ListenStarted += () => LogEnqueued("system", "LISTENING — awaiting command.");
        _sm.ListenStopped += () => LogEnqueued("system", "Listening stopped.");
        _audio = new AudioCapture();
        _audio.Samples += OnAudioSamples;
        _audio.Level += OnAudioLevel;
        _voiceId.WakeSpotted += OnWakeSpotted;
        _verifyWatch.Tick += (_, _) => CheckVerifyProgress();
        if (_settings.VoiceEnrolled)
        {
            _voiceId.LoadProfile(_settings.VoiceProfile);
            StartMonitor();
            AddMessage("system", "VOICE ID: wake-word monitor active.");
        }
        SetConfidence(0f);
        _appWindow = AppWindow;
        Title = "ULTRON";

        // Native caption/title bar removed; our HUD header owns the top row.
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter is null)
        {
            System.Diagnostics.Debug.WriteLine("ULTRON: presenter null (unpackaged)");
            presenter = OverlappedPresenter.Create();
            AppWindow.SetPresenter(presenter);
        }
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsMaximizable = true;
        presenter.IsMinimizable = true;
        presenter.IsResizable = true;

        // Extend content into the title bar area and give the native caption
        // buttons/inactive strip a dark background so no white line shows.
        ExtendsContentIntoTitleBar = true;
        var tb = AppWindow.TitleBar;
        tb.BackgroundColor = Colors2(0x1E, 0x1F, 0x24);
        tb.InactiveBackgroundColor = Colors2(0x1E, 0x1F, 0x24);
        tb.ButtonBackgroundColor = Colors2(0x1E, 0x1F, 0x24);
        tb.ButtonInactiveBackgroundColor = Colors2(0x1E, 0x1F, 0x24);
        tb.ButtonHoverBackgroundColor = Colors2(0x33, 0x34, 0x3A);
        tb.ButtonHoverForegroundColor = Colors2(0x7C, 0x7E, 0x85);
        tb.ButtonPressedBackgroundColor = Colors2(0xFF, 0x2E, 0x2E);
        tb.ButtonPressedForegroundColor = Colors2(0xFF, 0xFF, 0xFF);
        SetTitleBar(TitleBarRoot);
        try { _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "ULTRON.exe")); }
        catch (Exception ex) { AppLog.Write("MainWindow", $"SetIcon failed: {ex.Message}", AppLog.Level.Warn); }
        _appWindow.Title = Title;

        // Belt-and-suspenders: darken the native non-client caption so no white
        // strip can show even if some WinUI builds leave the OS title bar up.
        try
        {
            _selfHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            NativeTheme.ApplyDarkCaption(_selfHwnd);
        }
        catch (Exception ex)
        {
            AppLog.Write("MainWindow", $"Dark caption failed: {ex.Message}", AppLog.Level.Warn);
            try { _selfHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this); } catch { }
        }

        // ===================== TRAY + HOTKEYS + NOTIFICATIONS =====================
        _tray = new TrayIcon();
        _tray.ShowRequested += ShowFromTray;
        _tray.ToggleAwakeRequested += ToggleAwake;
        _tray.ToggleMuteRequested += ToggleMicMute;
        _tray.PushToTalkRequested += TogglePtt;
        _tray.QuitRequested += Close;

        _hotkeys = new HotkeyService(_tray);
        _hotkeys.Pressed += OnHotkeyPressed;
        ApplyHotkeyBindings();

        NotificationService.EnsureAumidShortcut();
        _notify = new NotificationService(_tray);

        // Crash recovery watchdog: heartbeat + sibling guardian process.
        Watchdog.TouchHeartbeat();
        Watchdog.LaunchGuardianIfNeeded();
        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _heartbeatTimer.Tick += (_, _) => Watchdog.TouchHeartbeat();
        _heartbeatTimer.Start();

        // Call duration timer
        _callTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _callTimer.Tick += (_, _) => UpdateCallDuration();

        // Track the window the user is actually looking at, so type_text and
        // friends can inject input into the real target instead of our own window.
        try
        {
            var winTimer = DispatcherQueue.CreateTimer();
            winTimer.Interval = TimeSpan.FromMilliseconds(400);
            winTimer.IsRepeating = true;
            winTimer.Tick += (_, _) => TrackActiveWindow();
            winTimer.Start();
        }
        catch (Exception ex) { AppLog.Write("MainWindow", $"TrackActiveWindow timer failed: {ex.Message}", AppLog.Level.Warn); }

        _brain.ApprovalRequested += OnApprovalRequested;
        WireInputs();
        StartTelemetryLoop();
        StartOrbSpin();
        ApplyStateVisual(_sm.Current);

        if ((!_settings.SetupComplete || string.IsNullOrEmpty(_settings.GeminiApiKey))
            && Environment.GetEnvironmentVariable("ULTRON_SKIP_SETUP") != "1")
        {
            // Defer so a XamlRoot exists before showing the setup ContentDialog.
            DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(250);
                await OpenSettingsAsync();
            });
        }
        else
        {
            AddMessage("system", "CORE ONLINE");
        }

        _repo = new ModelRepo(AppSettings.DataDir());
        _repo.Progress += OnModelProgress;

        LoadModelsAsync();
    }

    private void OnModelProgress(string key, string size, double pct)
    {
        DispatcherQueue.TryEnqueue(() => TickerText.Text = $"DOWNLOAD: {key} {size} ({pct:P0})");
    }

    /// <summary>
    /// Composition root for the tool layer: the one place that is allowed to know
    /// both the UI and the tool handlers.
    /// </summary>
    /// <remarks>
    /// The lambdas below are scaffolding. As each domain moves out (STEP 3+), its
    /// entry is replaced by a real service - e.g. FileToolService - that receives
    /// only the dependencies it needs and never sees this window.
    /// </remarks>
    private ToolRouter BuildToolRouter()
    {
        // Legacy handlers still take Dictionary<string, object>. ToLegacyArgs is
        // deleted in STEP 11, once every handler owns its typed arguments.
        //
        // Sync/Async used to wrap whatever the handler returned in ToolResult.Ok,
        // so a handler that returned "Unrecognized key: X" or "this tool is not
        // implemented" reached the model as a success and the model would report
        // the failure back to the user as done. These adapters no longer wrap:
        // a handler that can fail returns its own ToolResult.
        IToolHandler Sync(string name, Func<Dictionary<string, object>, ToolResult> run) =>
            new DelegateToolHandler(name, (toolCall, _) => run(ToLegacyArgs(toolCall.Arguments)));

        IToolHandler Async(string name, Func<Dictionary<string, object>, Task<ToolResult>> run) =>
            new DelegateToolHandler(name, (toolCall, _) => run(ToLegacyArgs(toolCall.Arguments)));

        // For the few handlers with no failure path at all, where the message is
        // always a true success. Anything that can fail must use Sync/Async.
        IToolHandler SyncOk(string name, Func<Dictionary<string, object>, string> run) =>
            new DelegateToolHandler(name, (toolCall, _) => ToolResult.Ok(run(ToLegacyArgs(toolCall.Arguments))));

        IToolHandler Passthrough(string name, string message) =>
            new DelegateToolHandler(name, (_, _) => ToolResult.Ok(message));

        return new ToolRouter(
            new IToolHandler[]
            {
                // memory
                Sync("save_memory", HandleSaveMemory),
                Sync("recall_memory", HandleRecallMemory),
                // desktop input
                Async("type_text", HandleTypeTextAsync),
                Sync("press_key", HandlePressKey),
                Async("desktop_control", HandleDesktopControl),
                Sync("window_manage", HandleWindowManage),
                // system
                SyncOk("system_status", _ => HandleSystemStatus()),
                // apps
                new AppToolService(new AppResolver(), new SystemProcessLauncher(), this, _undo),
                Async("computer_settings", HandleComputerSettings),
                SyncOk("set_away_mode", HandleSetAway),
                SyncOk("shutdown_jarvis", _ => Shutdown()),
                // web / media
                Async("web_search", HandleWebSearchAsync),
                Async("weather_report", HandleWeatherAsync),
                Async("browser_control", HandleBrowserControlAsync),
                Sync("youtube_video", HandleYouTubeVideo),
                // files
                new FileToolService(new UserProfilePathPolicy(), _undo),
                // communication / misc
                Sync("send_message", HandleSendMessage),
                Sync("reminder", HandleReminder),
                Sync("code_helper", HandleCodeHelper),
                Sync("undo", _ => HandleUndo()),
                // handled by the backend process, never executed here
                Passthrough("screen_process", "Vision handled by the backend."),
                Passthrough("close_camera", "Camera closed."),
                Passthrough("set_voice", "Voice handled by the backend."),
            },
            new PolicyEngine(),
            this);
    }

    private async void LoadModelsAsync()
    {
        try
        {
            // Gemini Live does STT+TTS+VAD server-side, so skip heavy local
            // Whisper/VAD loading unless we have no Gemini key (pure local
            // mode) or Gemini later drops (lazy fallback in OnGeminiDisconnected).
            bool localMode = string.IsNullOrEmpty(_settings.GeminiApiKey);
            Dbg($"LoadModels: start (localMode={localMode})");
            if (localMode)
            {
                await EnsureLocalModels();
            }
            else
            {
                DispatcherQueue.TryEnqueue(() => TickerText.Text = "CORE STATUS: GEMINI MODE (local STT/TTS skipped)");
                _ = EnsureVadAsync();
            }

            Dbg("LoadModels: starting Gemini backend");
            _ = Task.Run(async () =>
            {
                await _gemini.StartAsync(_settings.GeminiApiKey ?? "", _settings.GeminiVoice);
                if (_micMuted) await _gemini.SetMicMutedAsync(true);
            });

            // First-run friendliness: verify the runtime prerequisites that aren't
            // covered by the normal load path (python, backend payload, models, adb).
            _ = RunEnvChecksAsync();
        }
        catch (Exception ex)
        {
            Dbg("LoadModels: FAILED " + ex);
            DispatcherQueue.TryEnqueue(() => AddMessage("system", "Model load failed: " + ex.Message));
        }
    }

    /// <summary>Non-blocking startup diagnostics; surfaces missing prerequisites
    /// once in the chat + debug log instead of silently failing later.</summary>
    private async Task RunEnvChecksAsync()
    {
        try
        {
            var warnings = new List<string>();
            var python = FindEnvPython();
            if (python is null)
            {
                warnings.Add("Python 3.11+ not found — the Gemini backend cannot start.");
            }
            else
            {
                var backend = Path.Combine(AppContext.BaseDirectory, "backend", "gemini_backend.py");
                if (!File.Exists(backend))
                    warnings.Add("backend/gemini_backend.py is missing from the install.");
            }
            var missingModels = EnvModelKeys.Where(k => !_repo.Has(k)).ToList();
            if (missingModels.Count > 0)
                warnings.Add("Models not downloaded yet: " + string.Join(", ", missingModels));

            foreach (var w in warnings) Dbg("ENV: " + w);
            if (warnings.Count > 0)
                DispatcherQueue.TryEnqueue(() => AddMessage("system", "FIRST-RUN CHECKS:\n" + string.Join("\n", warnings)));
        }
        catch (Exception ex)
        {
            Dbg("ENV check failed: " + ex.Message);
        }
    }

    private async Task EnsureVadAsync()
    {
        try
        {
            if (_repo.Has("silero-vad.onnx")) return;
            Dbg("LoadModels: downloading silero VAD gate");
            DispatcherQueue.TryEnqueue(() => TickerText.Text = "DOWNLOAD: silero-vad.onnx (voice gate)");
            await _repo.EnsureAsync("silero-vad.onnx");
            Dbg("LoadModels: silero VAD ready");
        }
        catch (Exception ex)
        {
            Dbg("LoadModels: VAD download failed, will stream raw mic: " + ex.Message);
        }
    }

    private static readonly string[] EnvModelKeys =
    [
        "whisper-encoder.onnx",
        "whisper-decoder.onnx",
        "whisper-tokenizer.json",
        "silero-vad.onnx",
    ];

    /// <summary>Locate a real CPython with the Gemini SDK (mirrors GeminiBackend.FindPython).</summary>
    private static string? FindEnvPython()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var options = new[] { 312, 313, 311, 310 };
        foreach (var ver in options)
        {
            var exe = Path.Combine(local, "Programs", "Python", $"Python{ver}", "python.exe");
            if (File.Exists(exe)) return exe;
        }
        foreach (var shim in new[] { "python3", "python" })
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(shim, "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(2000);
                if (p?.ExitCode == 0) return shim;
            }
            catch (Exception ex) { AppLog.Write("MainWindow", $"FindEnvPython shim failed: {ex.Message}", AppLog.Level.Debug); }
        }
        return null;
    }

    private readonly object _localLoadLock = new();
    private Task? _localLoadTask;
    private bool _localModelsRequested;

    private Task EnsureLocalModels()
    {
        lock (_localLoadLock)
        {
            if (_localModelsRequested && _modelsLoaded) return Task.CompletedTask;
            if (_localLoadTask is not null) return _localLoadTask;
            _localModelsRequested = true;
            _localLoadTask = Task.Run(async () =>
            {
                try
                {
                    Dbg("LoadModels: ensure local models begin");
                    DispatcherQueue.TryEnqueue(() => TickerText.Text = "LOADING: fallback AI models...");
                    await _repo.EnsureAllAsync();
                    var dir = _repo.Dir;
                    var whisperTask = Task.Run(() => { var w = new WhisperStt(); w.Load(dir); return w; });
                    var vadTask = Task.Run(() => new SileroVad(_repo.PathFor("silero-vad.onnx")));
                    await Task.WhenAll(whisperTask, vadTask);
                    _whisper = await whisperTask;
                    _vad = await vadTask;
                    _modelsLoaded = true;
                    Dbg("LoadModels: ensure local models done");
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        TickerText.Text = "CORE STATUS: ALL MODELS LOADED";
                        AddMessage("system", "ONLINE — Whisper STT + Silero VAD ready.");
                    });
                }
                catch (Exception ex)
                {
                    Dbg("LoadModels: local load FAILED " + ex);
                }
            });
            return _localLoadTask;
        }
    }

    private static Color Colors2(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    private void OnApprovalRequested(ApprovalRequest req)
    {
        void Show()
        {
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = Content?.XamlRoot,
                    Title = "APPROVAL REQUIRED",
                    Content = req.Description,
                    PrimaryButtonText = "Approve",
                    CloseButtonText = "Deny",
                };
                if (dialog.XamlRoot == null) { req.Resolve?.Invoke(false); return; }
                _ = dialog.ShowAsync().AsTask().ContinueWith(
                    async t => req.Resolve?.Invoke(await t == ContentDialogResult.Primary),
                    System.Threading.Tasks.TaskScheduler.Default);
            }
            catch
            {
                req.Resolve?.Invoke(false);
            }
        }
        if (DispatcherQueue == null) { req.Resolve?.Invoke(false); return; }
        if (DispatcherQueue.HasThreadAccess) Show();
        else DispatcherQueue.TryEnqueue(Show);
    }

    private async Task OpenSettingsAsync()
    {
        var voiceOptions = new ComboBox
        {
            Width = 360,
            ItemsSource = new[] { "Charon", "Puck", "Kore", "Fenrir", "Aoede" },
            SelectedItem = _settings.GeminiVoice,
        };
        var memoryToggle = new ToggleSwitch { Header = "Store conversations in local memory", IsOn = _settings.MemoryLogging };
        var purgeBtn = new Button { Content = "Purge ALL stored memory now", HorizontalAlignment = HorizontalAlignment.Left };
        purgeBtn.Click += (_, _) =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _memory.PurgeAllAsync();
                    DispatcherQueue.TryEnqueue(() => AddMessage("system", "Local memory (conversations + facts) purged."));
                }
                catch (Exception ex) { Dbg($"purge failed: {ex.Message}"); }
            });
        };

        var keyBox = new TextBox { PlaceholderText = "AIza...", Width = 360 };
        var pinBox = new PasswordBox { PlaceholderText = "4+ characters", Width = 360 };
        var pttBox = new TextBox { Text = _settings.HotkeyPtt, Width = 180 };
        var muteBox = new TextBox { Text = _settings.HotkeyMute, Width = 180 };
        var wakeBox = new TextBox { Text = _settings.HotkeyWake, Width = 180 };
        var font = new Microsoft.UI.Xaml.Media.FontFamily("Consolas");
        TextBlock Lbl(string s, int size = 12) => new() { Text = s, FontFamily = font, FontSize = size };

        // ---------- TELEGRAM VOICE CALLS ----------
        var tgMuted = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 155, 161, 171));
        var tgAccent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 242, 201, 76));
        var tgToggle = new ToggleSwitch { Header = "Enable Telegram voice calling", IsOn = _settings.TelegramCallEnabled };
        var tgApiId = new TextBox { Text = _settings.TelegramApiId, Width = 360, PlaceholderText = "e.g. 12345678" };
        var tgApiHash = new PasswordBox { Width = 360, Password = _settings.TelegramApiHash };
        var tgPhone = new TextBox { Text = _settings.TelegramPhone, Width = 360, PlaceholderText = "+15551234567 (ULTRON account)" };
        _tgStatusText = new TextBlock
        {
            FontFamily = font,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = tgMuted,
            Text = _settings.TelegramCallEnabled ? "Status unknown — connect or restart to check the session." : "Telegram calling is off.",
        };
        var tgConnect = new Button { Content = "Connect", Padding = new Thickness(8, 6, 8, 6) };
        var tgLogout = new Button { Content = "Logout", Padding = new Thickness(8, 6, 8, 6) };
        var tgTarget = new TextBox { Text = _settings.TelegramCallTarget, Width = 360, PlaceholderText = "@yourusername or phone to call" };
        var tgResolve = new Button { Content = "Resolve Target", Padding = new Thickness(8, 6, 8, 6) };
        var tgFallbackEnabled = new ToggleSwitch { Header = "Enable call fallback messaging", IsOn = _settings.CallFallbackEnabled };
        var tgFallbackThreshold = new TextBox { Text = _settings.CallFallbackThresholdSeconds.ToString(), Width = 80, PlaceholderText = "5" };
        var tgFallbackChatId = new TextBox { Text = _settings.CallFallbackChatId, Width = 200, PlaceholderText = "@username or chat ID" };
        var tgHint = new TextBlock
        {
            FontFamily = font,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Foreground = tgMuted,
            Text = "How to get credentials: sign in on my.telegram.org with the ULTRON account's phone " +
                   "number, open ‘API development tools’, and copy the api_id and api_hash below. " +
                   "This dedicated account is what places (and later answers) the voice call. " +
                   "Credentials are stored encrypted with your Windows login, same as the Gemini key.",
        };
        tgConnect.Click += async (_, _) =>
        {
            var id = tgApiId.Text.Trim();
            var hash = tgApiHash.Password.Trim();
            var phone = tgPhone.Text.Trim();
            if (id.Length == 0 || hash.Length == 0)
            {
                _tgStatusText.Text = "Enter api_id and api_hash before connecting.";
                _tgStatusText.Foreground = tgAccent;
                return;
            }
            lock (_settings)
            {
                _settings.TelegramApiId = id;
                _settings.TelegramApiHash = hash;
                _settings.TelegramPhone = phone;
                _settings.TelegramCallEnabled = tgToggle.IsOn;
                _settings.Save();
            }
            _gemini.TelegramOptions = new TelegramCallOptions(id, hash, phone, tgToggle.IsOn, tgTarget.Text.Trim());
            _tgStatusText.Text = "Connecting to Telegram…";
            _tgStatusText.Foreground = tgMuted;
            await _gemini.SendTelegramLoginAsync(phone, id, hash);
        };
        tgResolve.Click += async (_, _) =>
        {
            var target = tgTarget.Text.Trim();
            if (target.Length == 0)
            {
                _tgStatusText.Text = "Enter a call target first.";
                _tgStatusText.Foreground = tgAccent;
                return;
            }
            lock (_settings)
            {
                _settings.TelegramCallTarget = target;
                _settings.Save();
            }
            _tgStatusText.Text = $"Resolving {target}…";
            _tgStatusText.Foreground = tgMuted;
            await _gemini.SendTelegramResolveTargetAsync(target);
        };
        
        // Test Call button - find it in the dialog and add click handler
        // We'll add it after the dialog is created
        // (handled via the dialog's content tree)

        tgLogout.Click += async (_, _) =>
        {
            _tgStatusText.Text = "Logging out of Telegram…";
            _tgStatusText.Foreground = tgMuted;
            await _gemini.SendTelegramLogoutAsync();
        };

        var tgCodePanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        var tgCodeBox = new TextBox { PlaceholderText = "Login code (or 2FA password)", Width = 360 };
        var tgCodeSubmit = new Button
        {
            Content = "Submit Code",
            Padding = new Thickness(8, 6, 8, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        tgCodeSubmit.Click += async (_, _) =>
        {
            var code = tgCodeBox.Text.Trim();
            if (code.Length == 0) return;
            tgCodeSubmit.IsEnabled = false;
            if (_tgStatusText is not null) _tgStatusText.Text = "Submitting code…";
            await _gemini.SendTelegramCodeAsync(code);
            tgCodeSubmit.IsEnabled = true;
            tgCodeBox.Text = "";
        };
        tgCodePanel.Children.Add(Lbl("Enter the login code Telegram sent (check the ULTRON account):"));
        tgCodePanel.Children.Add(tgCodeBox);
        tgCodePanel.Children.Add(tgCodeSubmit);
        _tgCodePanel = tgCodePanel;
        _tgCodeBox = tgCodeBox;

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "ULTRON SETUP",
            Content = new ScrollViewer
            {
                MaxHeight = 430,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Spacing = 10,
                    Width = 380,
                    Children =
                    {
                        Lbl("Paste your Gemini API key (aistudio.google.com/apikey):"),
                        keyBox,
                        Lbl("Voice:"),
                        voiceOptions,
                        Lbl("Guard PIN (unlocks voice-fingerprint reset):"),
                        pinBox,
                        Lbl("GLOBAL HOTKEYS (Win / Ctrl / Alt / Shift, plus a key or F1-F12):"),
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Lbl("Push-to-talk ", 11), pttBox } },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Lbl("Mute mic     ", 11), muteBox } },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Lbl("Wake toggle  ", 11), wakeBox } },
                        Lbl("TELEGRAM VOICE CALLS — real phone calls from the ULTRON account:"),
                        tgToggle,
                        Lbl("api_id (your ULTRON app's id at my.telegram.org):"),
                        tgApiId,
                        Lbl("api_hash (shown once at my.telegram.org):"),
                        tgApiHash,
                        Lbl("ULTRON account phone number (calls come from this):"),
                        tgPhone,
                        tgHint,
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { tgConnect, tgLogout } },
                        _tgStatusText,
                        Lbl("Call target (the Telegram account that should be called):"),
                        tgTarget,
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { tgResolve } },
                        tgCodePanel,

                        // Test Call button
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = {
                            Lbl("Test Call:", 11),
                            new Button { Content = "📞 Test Call", Padding = new Thickness(12, 6, 12, 6),
                                Background = new SolidColorBrush(Microsoft.UI.Colors.DarkGreen), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                                CornerRadius = new CornerRadius(6), FontFamily = font, FontSize = 11 }
                        }},

                        // Call fallback settings
                        Lbl("CALL FALLBACK — send Telegram message if call unanswered or hung up immediately:"),
                        new ToggleSwitch { Header = "Enable call fallback messaging", IsOn = _settings.CallFallbackEnabled },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = {
                            Lbl("Hangup threshold (seconds):", 11),
                            new TextBox { Text = _settings.CallFallbackThresholdSeconds.ToString(), Width = 80, PlaceholderText = "5" }
                        }},
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = {
                            Lbl("Fallback chat ID (optional override):", 11),
                            new TextBox { Text = _settings.CallFallbackChatId, Width = 200, PlaceholderText = "@username or chat ID" }
                        }},

                        Lbl("PRIVACY:"),
                        memoryToggle,
                        purgeBtn,
                    }
                },
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Skip",
        };
        dialog.PrimaryButtonClick += (_, _) =>
        {
            var key = keyBox.Text.Trim();
            var previousKey = _settings.GeminiApiKey;
            var previousVoice = _settings.GeminiVoice;
            lock (_settings)
            {
                _settings.MemoryLogging = memoryToggle.IsOn;
                _settings.TelegramCallEnabled = tgToggle.IsOn;
                _settings.CallFallbackEnabled = tgFallbackEnabled.IsOn;
                int.TryParse(tgFallbackThreshold.Text.Trim(), out var fbThr);
                if (fbThr > 0) _settings.CallFallbackThresholdSeconds = fbThr;
                _settings.CallFallbackChatId = tgFallbackChatId.Text.Trim();
                if (tgApiId.Text.Trim().Length > 0) _settings.TelegramApiId = tgApiId.Text.Trim();
                if (tgApiHash.Password.Trim().Length > 0) _settings.TelegramApiHash = tgApiHash.Password.Trim();
                if (tgPhone.Text.Trim().Length > 0) _settings.TelegramPhone = tgPhone.Text.Trim();
                if (!string.IsNullOrEmpty(key))
                {
                    _settings.GeminiApiKey = key;
                    _settings.GeminiVoice = voiceOptions.SelectedItem?.ToString() ?? "Charon";
                    if (pinBox.Password.Length >= 4) _settings.SetPin(pinBox.Password);
                    _settings.SetupComplete = true;
                }

                var notifications = new System.Collections.Generic.List<string>();
                foreach (var (spec, setter) in new[]
                         {
                             (pttBox.Text.Trim(), (Action<string>)(v => _settings.HotkeyPtt = v)),
                             (muteBox.Text.Trim(), (Action<string>)(v => _settings.HotkeyMute = v)),
                             (wakeBox.Text.Trim(), (Action<string>)(v => _settings.HotkeyWake = v)),
                         })
                {
                    if (HotkeyService.TryParse(spec, out _, out _)) setter(spec);
                    else notifications.Add($"'{spec}' isn't a valid hotkey — left unchanged.");
                }


                ApplyHotkeyBindings();

                foreach (var n in notifications) AddMessage("system", n);
            }
            if (!string.IsNullOrEmpty(key))
            {
                var restartBackend = previousKey != _settings.GeminiApiKey || previousVoice != _settings.GeminiVoice;
                if (restartBackend)
                    _ = Task.Run(async () => await _gemini.StartAsync(_settings.GeminiApiKey, _settings.GeminiVoice));
                AddMessage("system", $"CORE ONLINE — Gemini brain configured (voice: {voiceOptions.SelectedItem?.ToString() ?? "Charon"})");
            }
            else
                AddMessage("system", "Settings saved.");
        };
        try
        {
            // Wire up Test Call button after dialog content is set
            if (dialog.Content is ScrollViewer sv && sv.Content is StackPanel sp)
            {
                foreach (var child in sp.Children)
                {
                    if (child is StackPanel testPanel && testPanel.Orientation == Orientation.Horizontal)
                    {
                        foreach (var tc in testPanel.Children)
                        {
                            if (tc is Button btn && btn.Content?.ToString()?.Contains("Test Call") == true)
                            {
                                btn.Click += async (_, _) => await _gemini.SendTelegramCallStartAsync();
                                break;
                            }
                        }
                    }
                }
            }

            await dialog.ShowAsync();
        }
        finally
        {
            _tgStatusText = null;
            _tgCodePanel = null;
            _tgCodeBox = null;
        }
    }

    /// <summary>Collapse + release the inline code entry controls (called after a
    /// successful login, or when settings closes).</summary>
    private void HideTelegramCodePanel()
    {
        if (_tgCodePanel is not null) _tgCodePanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Ask the user for the Telegram login code (or 2FA password) that
    /// arrived on the ULTRON account, then forward it to the backend. When the
    /// settings dialog is open the entry appears inline in it; otherwise a
    /// standalone dialog is used.</summary>
    private void PromptTelegramCode(string phone, string hint)
    {
        try
        {
            if (_tgCodePanel is not null)
            {
                _tgCodeBox!.Text = "";
                _tgCodePanel.Visibility = Visibility.Visible;
                _tgCodeBox.Focus(FocusState.Programmatic);
                if (_tgStatusText is not null) _tgStatusText.Text = $"Enter the Telegram code sent to {phone}.";
                return;
            }
            var codeBox = new TextBox { PlaceholderText = "Login code", Width = 300 };
            var dlg = new ContentDialog
            {
                XamlRoot = Content?.XamlRoot,
                Title = "TELEGRAM LOGIN",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = $"Enter the Telegram login code for {phone}.", TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = hint, FontSize = 11, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 155, 161, 171)) },
                        codeBox,
                    },
                },
                PrimaryButtonText = "Submit",
                CloseButtonText = "Cancel",
            };
            if (dlg.XamlRoot == null)
            {
                AddMessage("system", $"Telegram needs a login code: {hint}");
                return;
            }
            _ = dlg.ShowAsync().AsTask().ContinueWith(async t =>
            {
                if (t.IsCompletedSuccessfully && t.Result == ContentDialogResult.Primary && codeBox.Text.Trim().Length > 0)
                    await _gemini.SendTelegramCodeAsync(codeBox.Text.Trim());
            }, System.Threading.Tasks.TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Dbg($"code prompt failed: {ex.Message}");
            AddMessage("system", $"Telegram needs a login code: {hint}");
        }
    }

    private void WireInputs()
    {
        ConversationList.Items.Clear();
    }

}
