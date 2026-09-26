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
using QRCoder;

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
    internal DashboardServer? _dashboard;
    internal string? _dashboardUrl;
    private volatile bool _micMuted;
    internal DispatcherTimer? _heartbeatTimer;
    internal DispatcherTimer? _callTimer;
    internal TimeSpan _callDuration;
    private const int HotkeyPtt = 1, HotkeyMute = 2, HotkeyWake = 3;

    // Centralized tool registry with authorization metadata
    private readonly ToolRouter _toolRouter;
    private readonly UndoLedger _undo = new();

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
            _dashboard?.Dispose();
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

        _tray.DashboardRequested += OnDashboardRequested;

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

        // LAN web deck (QR in Setup): live read-only mirror of the conversation.
        if (_settings.WebDashboardEnabled)
        {
            EnsureDashboard(_settings.WebDashboardPort);
            if (_dashboardUrl is not null)
                AddMessage("system", $"Web deck live — scan the QR in Setup or open {_dashboardUrl}");
        }

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
        IToolHandler Sync(string name, Func<Dictionary<string, object>, string> run) =>
            new DelegateToolHandler(name, (toolCall, _) => ToolResult.Ok(run(ToLegacyArgs(toolCall.Arguments))));

        IToolHandler Async(string name, Func<Dictionary<string, object>, Task<string>> run) =>
            new DelegateToolHandler(name, async (toolCall, _) =>
                ToolResult.Ok(await run(ToLegacyArgs(toolCall.Arguments))));

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
                Sync("desktop_control", HandleDesktopControl),
                Sync("window_manage", HandleWindowManage),
                // system
                Sync("system_status", _ => HandleSystemStatus()),
                // apps
                new AppToolService(new AppResolver(), new SystemProcessLauncher(), this, _undo),
                Sync("computer_settings", HandleComputerSettings),
                Sync("set_away_mode", HandleSetAway),
                Sync("shutdown_jarvis", _ => Shutdown()),
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

        var webToggle = new ToggleSwitch { Header = "Enable LAN web deck (scan the QR on your phone/TV)", IsOn = _settings.WebDashboardEnabled };
        var portBox = new TextBox { Text = _settings.WebDashboardPort.ToString(), Width = 90 };
        var qrImage = new Image { Width = 216, Height = 216, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        var qrUrl = new TextBlock
        {
            FontFamily = font,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 155, 161, 171)),
        };
        void RefreshQr()
        {
            if (!int.TryParse(portBox.Text.Trim(), out var p) || p is < 1 or > 65535)
            {
                qrImage.Source = null;
                qrUrl.Text = "Port must be 1–65535.";
                return;
            }
            var token = _dashboard?.Token ?? DashboardServer.NewToken();
            qrImage.Source = BuildDeckQr(p, token);
            qrUrl.Text = DeckUrl(p, token);
        }
        portBox.TextChanged += (_, _) => RefreshQr();
        RefreshQr();

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
                        Lbl("WEB DECK — read-only conversation mirror for your phone/TV (QR):"),
                        webToggle,
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Lbl("Port  ", 11), portBox } },
                        qrImage,
                        qrUrl,
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

                var webPort = int.TryParse(portBox.Text.Trim(), out var p) && p is >= 1 and <= 65535 ? p : _settings.WebDashboardPort;
                var webEnabled = webToggle.IsOn;
                if (webEnabled != _settings.WebDashboardEnabled || webPort != _settings.WebDashboardPort)
                {
                    _settings.WebDashboardEnabled = webEnabled;
                    _settings.WebDashboardPort = webPort;
                    if (webEnabled)
                    {
                        EnsureDashboard(webPort);
                        AddMessage("system", _dashboardUrl is not null
                            ? $"Web deck live — scan the QR or open {_dashboardUrl}"
                            : "Web deck failed to start. See ultron.log.");
                    }
                    else
                    {
                        _dashboard?.Dispose();
                        _dashboard = null;
                        _dashboardUrl = null;
                        AddMessage("system", "Web deck disabled.");
                    }
                }

                _settings.Save();
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

    /* ===================== WINDOW CONTROLS ===================== */

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        // Minimize-to-tray: drop out of the taskbar; the tray icon stays alive.
        _appWindow.Hide();
        var wake = HotkeyService.TryParse(_settings.HotkeyWake, out var mods, out var vk)
            ? HotkeyService.ToDisplay(mods, vk)
            : "Win+Alt+L";
        _tray?.ShowBalloon("ULTRON", $"Still running in the tray. {wake} to wake it.");
    }

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            if (p.State == OverlappedPresenterState.Maximized) p.Restore();
            else p.Maximize();
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /* ===================== NAV RAIL ===================== */

    private Button? _activeNav;

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (_activeNav is not null)
        {
            _activeNav.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            if (_activeNav.Content is FontIcon prev) prev.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 201, 206, 214));
        }
        _activeNav = btn;
        btn.Background = (Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["UltronCrimsonBrush"];
        if (btn.Content is FontIcon cur) cur.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));

        // Secondary view placeholder text.
        (SecondaryViewTitle.Text, SecondaryViewBody.Text) = btn.Tag switch
        {
            "commandCenter" => ("COMMAND CENTER", "Voice + text interface. Wake word, conversation mode, phone control and approvals all live here."),
            "systemMonitor" => ("SYSTEM MONITOR", "Live process/thread/sensor telemetry charts will render here on GPU (Composition/Win2D)."),
            "automation" => ("AUTOMATION & SCRIPTS", "Phone routines (locate, ring, SMS, screenshot, unlock) and scripted tool actions will be managed here."),
            "directives" => ("LOGS & DIRECTIVES", "Conversation ledger, executed-command log and Ultron's directive/permission rules will land here."),
            _ => ("", ""),
        };

        // Memory panel is its own dedicated central view.
        var isMemory = (btn.Tag as string) == "memory";
        MemoryView.Visibility = isMemory ? Visibility.Visible : Visibility.Collapsed;
        OrbContainer.Visibility = isMemory ? Visibility.Collapsed : Visibility.Visible;
        OrbStateLabel.Visibility = isMemory ? Visibility.Collapsed : Visibility.Visible;
        ConversationList.Visibility = isMemory ? Visibility.Collapsed : Visibility.Visible;
        if (isMemory) RefreshMemory();
    }

    /* ===================== MEMORY PANEL ===================== */

    private static string MemoryFilePath =>
        Path.Combine(AppSettings.DataDir(), "long_term.json");

    private static bool _memoryJsonCorrupt;

    private Dictionary<string, Dictionary<string, object?>> LoadMemoryJson()
    {
        try
        {
            if (File.Exists(MemoryFilePath))
            {
                var root = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object?>>>(File.ReadAllText(MemoryFilePath));
                if (root is not null)
                {
                    _memoryJsonCorrupt = false;
                    return root;
                }
            }
        }
        catch
        {
            _memoryJsonCorrupt = true;
        }
        return new();
    }

    private static readonly JsonSerializerOptions MemoryJsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private void SaveMemoryJson(Dictionary<string, Dictionary<string, object?>> root)
    {
        if (_memoryJsonCorrupt)
            throw new InvalidOperationException("Memory store is corrupt and was not overwritten.");
        Directory.CreateDirectory(Path.GetDirectoryName(MemoryFilePath)!);
        var temporary = MemoryFilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(root, MemoryJsonOpts));
        File.Move(temporary, MemoryFilePath, true);
    }

    private string RecallFromMemoryJson(string query)
    {
        var root = LoadMemoryJson();
        var q = query ?? "";
        var results = new List<string>();
        foreach (var (cat, entries) in root)
        {
            if (entries is null) continue;
            foreach (var (key, val) in entries)
            {
                var value = val is Dictionary<string, object?> d && d.TryGetValue("value", out var v) ? v?.ToString() : val?.ToString();
                value ??= "";
                if (q.Length == 0 ||
                    cat.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    key.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    value.Contains(q, StringComparison.OrdinalIgnoreCase))
                    results.Add($"[{cat}] {key}: {value}");
            }
        }
        return results.Count > 0 ? string.Join("\n", results.Take(8)) : "Nothing found in memory.";
    }

    private sealed class MemEntry
    {
        public required string Category { get; init; }
        public required string Key { get; init; }
        public string? Value { get; set; }
        public string? Updated { get; init; }
    }

    private List<MemEntry> _memCache = new();

    private void RefreshMemory()
    {
        _memCache = LoadMemory();
        FilterMemory();
    }

    private List<MemEntry> LoadMemory()
    {
        var list = new List<MemEntry>();
        try
        {
            if (!File.Exists(MemoryFilePath)) return list;
            var root = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(File.ReadAllText(MemoryFilePath));
            if (root is null) return list;
            foreach (var (cat, entries) in root)
            {
                foreach (var (key, el) in entries)
                {
                    string? val = null, upd = null;
                    if (el.ValueKind == JsonValueKind.Object)
                    {
                        val = el.GetProperty("value").GetString();
                        upd = el.TryGetProperty("updated", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    }
                    else if (el.ValueKind == JsonValueKind.String)
                    {
                        val = el.GetString();
                    }
                    list.Add(new MemEntry { Category = cat, Key = key, Value = val, Updated = upd });
                }
            }
            list.Sort((a, b) => string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase) == 0
                ? string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase)
                : string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) { AppLog.Write("MainWindow", $"LoadMemory failed: {ex.Message}", AppLog.Level.Error); }
        return list;
    }

    private void FilterMemory()
    {
        MemoryList.Items.Clear();
        var q = MemorySearch.Text?.Trim() ?? "";
        var shown = 0;
        string? lastCat = null;
        foreach (var m in _memCache)
        {
            if (q.Length > 0 &&
                !m.Category.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !m.Key.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !(m.Value ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            if (m.Category != lastCat)
            {
                MemoryList.Items.Add(new TextBlock
                {
                    Text = m.Category.ToUpperInvariant(),
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 179, 3)),
                    Margin = new Thickness(0, 12, 0, 4),
                });
                lastCat = m.Category;
            }
            MemoryList.Items.Add(BuildMemoryRow(m));
            shown++;
        }
        MemoryCountText.Text = _memCache.Count == 0 ? "(empty)" : $"{shown}/{_memCache.Count}";
        MemoryHint.Visibility = _memCache.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Microsoft.UI.Xaml.Controls.StackPanel BuildMemoryRow(MemEntry m)
    {
        var row = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 6) };
        var head = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var key = new TextBlock
        {
            Text = m.Key,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 237, 240, 245)),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var stamp = new TextBlock
        {
            Text = m.Updated ?? "",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 10,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 155, 161, 171)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var editBtn = new Button
        {
            Content = "EDIT",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = m,
        };
        editBtn.Click += async (_, _) => await EditMemoryAsync(m);
        var delBtn = new Button
        {
            Content = "DEL",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(51, 255, 46, 46)),
            Tag = m,
        };
        delBtn.Click += (_, _) => DeleteMemory(m);
        head.Children.Add(key);
        head.Children.Add(stamp);
        head.Children.Add(editBtn);
        head.Children.Add(delBtn);
        row.Children.Add(head);
        row.Children.Add(new TextBlock
        {
            Text = m.Value ?? "",
            FontSize = 12,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 201, 206, 214)),
            TextWrapping = TextWrapping.Wrap,
        });
        return row;
    }

    private void SaveMemory()
    {
        var root = new Dictionary<string, Dictionary<string, object>>();
        foreach (var m in _memCache)
        {
            if (!root.TryGetValue(m.Category, out var cat))
            {
                cat = new Dictionary<string, object>();
                root[m.Category] = cat;
            }
            cat[m.Key] = new Dictionary<string, object?>
            {
                ["value"] = m.Value ?? "",
                ["updated"] = m.Updated ?? DateTime.Now.ToString("yyyy-MM-dd"),
            };
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MemoryFilePath)!);
            File.WriteAllText(MemoryFilePath, JsonSerializer.Serialize(root, MemoryJsonOpts));
        }
        catch (Exception ex) { AppLog.Write("MainWindow", $"SaveMemoryJson failed: {ex.Message}", AppLog.Level.Error); }
    }

    private async Task EditMemoryAsync(MemEntry m)
    {
        var box = new TextBox
        {
            Text = m.Value ?? "",
            AcceptsReturn = true,
            MinHeight = 120,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var dlg = new ContentDialog
        {
            Title = $"Edit — [{m.Category}] {m.Key}",
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            m.Value = box.Text;
            SaveMemory();
            FilterMemory();
        }
    }

    private void DeleteMemory(MemEntry m)
    {
        _memCache.RemoveAll(x => x.Category == m.Category && x.Key == m.Key);
        SaveMemory();
        FilterMemory();
    }

    private void MemorySearch_TextChanged(object sender, TextChangedEventArgs e) => FilterMemory();

    private void MemoryRefresh_Click(object sender, RoutedEventArgs e) => RefreshMemory();

    private async void MemoryAdd_Click(object sender, RoutedEventArgs e)
    {
        var catBox = new TextBox { PlaceholderText = "category (e.g. preferences)", Margin = new Thickness(0, 8, 0, 0) };
        var keyBox = new TextBox { PlaceholderText = "key (e.g. favorite_color)", Margin = new Thickness(0, 8, 0, 0) };
        var valBox = new TextBox { PlaceholderText = "value", AcceptsReturn = true, MinHeight = 80, Margin = new Thickness(0, 8, 0, 0) };
        var panel = new Microsoft.UI.Xaml.Controls.StackPanel();
        panel.Children.Add(new TextBlock { Text = "Category (identity, preferences, projects, relationships, wishes, notes, ...)", FontSize = 11, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 155, 161, 171)) });
        panel.Children.Add(catBox);
        panel.Children.Add(keyBox);
        panel.Children.Add(valBox);
        var dlg = new ContentDialog
        {
            Title = "Add memory",
            Content = panel,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary &&
            !string.IsNullOrWhiteSpace(catBox.Text) && !string.IsNullOrWhiteSpace(keyBox.Text))
        {
            _memCache.RemoveAll(x => x.Category == catBox.Text.Trim() && x.Key == keyBox.Text.Trim());
            _memCache.Add(new MemEntry { Category = catBox.Text.Trim(), Key = keyBox.Text.Trim(), Value = valBox.Text, Updated = DateTime.Now.ToString("yyyy-MM-dd") });
            SaveMemory();
            RefreshMemory();
        }
    }

    private void MemoryClear_Click(object sender, RoutedEventArgs e)
    {
        _memCache.Clear();
        SaveMemory();
        RefreshMemory();
    }

    /* ===================== COMMAND BAR ===================== */

    internal StackPanel AddMessage(string who, string text)
    {
        var item = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };
        item.Children.Add(new TextBlock
        {
            Text = who.ToUpperInvariant(),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 10,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                who == "user"
                    ? Windows.UI.Color.FromArgb(255, 255, 179, 3)
                    : Windows.UI.Color.FromArgb(255, 255, 46, 46)),
        });
        item.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 237, 240, 245)),
            TextWrapping = TextWrapping.Wrap,
        });
        ConversationList.Items.Add(item);
        if (ConversationList.Items.Count > 200)
        {
            ConversationList.Items.RemoveAt(0);
        }
        ConversationList.ScrollIntoView(item);

        // Log to persistent memory store if enabled
        if (_settings.MemoryLogging && (who == "user" || who == "ultron"))
        {
            _ = Task.Run(async () =>
            {
                try { await _memory.LogAsync(who, text); }
                catch { /* swallow - memory logging is best-effort */ }
            });
        }

        return item;
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => SendCommand();

    private void MicButton_Click(object sender, RoutedEventArgs e)
    {
        _lastInteraction = DateTime.UtcNow;
        if (_geminiMode && _gemini.IsConnected) { ToggleAwake(); return; }
        if (_micMuted)
        {
            AddMessage("system", "Mic is muted — press Win+Alt+M to unmute.");
            return;
        }
        ToggleLocalPtt();
    }

    private void TogglePtt()
    {
        _lastInteraction = DateTime.UtcNow;
        if (_geminiMode && _gemini.IsConnected) { ToggleAwake(); return; }
        if (_micMuted) return;
        DispatcherQueue.TryEnqueue(() => ToggleLocalPtt());
    }

    private void ToggleLocalPtt()
    {
        // Manual awake/asleep override. With the wake gate armed, Gemini
        // starts asleep and only hears you after "Hey Ultron"; this button
        // forces the state either way. While Gemini talks the mic is
        // auto-muted (echo cancel), so speak after it finishes or wake it
        // again.
        if (_verifyBuffer is not null) { FinalizeVerify(); return; }
        if (_voiceId.OwnerEnrolled && _sm.Current is AssistantState.Sleep or AssistantState.Rest)
        {
            AddMessage("system", "PTT: speak your passphrase to verify.");
            StartWakeVerify();
            return;
        }
        if (_audio is not null && _audio.IsActive)
        {
            _audio.Stop();
            SetMicVisual(false);
            OrbPulse.ScaleX = OrbPulse.ScaleY = 1;
            if (_whisper is null || !_modelsLoaded) { AddMessage("system", "PTT released — models not loaded yet."); return; }
            var pcm = CaptureSnapshot();
            Dbg($"PTT release: pcm={pcm.Length}, sttQueued={_sttQueue.Count}, modelsLoaded={_modelsLoaded}");
            if (pcm.Length >= 3200)
            {
                _sttQueue.Enqueue(pcm);
                _ = PumpSttAsync();
            }
            else
                AddMessage("system", "PTT: no speech detected.");
            return;
        }
        var err = _audio?.Start() ?? "";
        if (err.Length > 0)
        {
            AddMessage("system", err);
            return;
        }
        _sm.EngageFromText();
        SetMicVisual(true);
        AddMessage("system", "PTT: listening... (speak, then release to transcribe).");
    }

    private void ToggleAwake()
    {
        _lastInteraction = DateTime.UtcNow;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_geminiMode && _gemini.IsConnected)
            {
                _awakeInGemini = !_awakeInGemini;
                SetMicVisual(_awakeInGemini);
                AddMessage("system", _awakeInGemini
                    ? "AWAKE — I'm listening (one command per wake)."
                    : "ASLEEP — press the mic or say \u201cHey Ultron\u201d to wake me.");
                _ = _gemini.SetAwakeAsync(_awakeInGemini);
                return;
            }
            if (_voiceId.OwnerEnrolled && _settings.VoiceEnrolled &&
                _sm.Current is AssistantState.Sleep or AssistantState.Rest)
            {
                AddMessage("system", "Wake toggle — verifying owner.");
                StartWakeVerify();
            }
            else
            {
                AddMessage("system", "Wake toggle — already awake.");
            }
        });
    }

    private void ToggleMicMute()
    {
        _micMuted = !_micMuted;
        _settings.MicMuted = _micMuted;
        try { _settings.Save(); } catch (Exception ex) { AppLog.Write("MainWindow", $"Settings save failed: {ex.Message}", AppLog.Level.Error); }
        _ = _gemini.SetMicMutedAsync(_micMuted);
        _dashboard?.PushMute(_micMuted);
        DispatcherQueue.TryEnqueue(() =>
        {
            SetMicVisual(false);
            var hotkey = HotkeyService.TryParse(_settings.HotkeyMute, out var m, out var v)
                ? HotkeyService.ToDisplay(m, v)
                : "Win+Alt+U";
            TickerText.Text = _micMuted ? $"MIC MUTED — {hotkey} to unmute." : "MIC UNMUTED";
            AddMessage("system", _micMuted ? "Mic muted — I can't hear you." : "Mic unmuted — listening.");
            _notify?.Notify("ULTRON", _micMuted
                ? "Microphone muted."
                : "Microphone unmuted.");
        });
    }

    private void ShowFromTray()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized)
                p.Restore();
            _appWindow.Show();
            Activate();
        });
    }

    /* ===================== LAN WEB DECK ===================== */

    internal void EnsureDashboard(int port)
    {
        _dashboard?.Dispose();
        _dashboard = new DashboardServer(port);
        _dashboardUrl = _dashboard.Start()
            ? DashboardServer.LanUrl(_dashboard.ActualPort) + "?k=" + _dashboard.Token
            : null;
        if (_dashboardUrl is not null)
            Dbg($"web deck: {_dashboardUrl}");
        else
            Dbg($"web deck failed to start: {_dashboard.LastError}");
    }

    private void OnDashboardRequested()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_dashboardUrl is null)
            {
                _ = OpenSettingsAsync();
                return;
            }
            _tray?.ShowBalloon("ULTRON WEB DECK", _dashboardUrl);
        });
    }

    private static string DeckUrl(int port, string token) =>
        DashboardServer.LanUrl(port) + "?k=" + token;

    private WriteableBitmap BuildDeckQr(int port, string token, int px = 240)
    {
        var qr = new QRCodeGenerator();
        var data = qr.CreateQrCode(DeckUrl(port, token), QRCodeGenerator.ECCLevel.M, forceUtf8: true);
        var matrix = data.ModuleMatrix;
        var size = matrix.Count;
        var scale = Math.Max(1, px / (size + 8));
        var dim = (size + 8) * scale;
        var buf = new byte[dim * dim * 4];
        for (var y = 0; y < dim; y++)
        {
            for (var x = 0; x < dim; x++)
            {
                var m = (x / scale) - 4;
                var n = (y / scale) - 4;
                var dark = m >= 0 && n >= 0 && m < size && n < size && matrix[n][m];
                var i = (y * dim + x) * 4;
                if (dark)
                {
                    buf[i] = 0x1A; buf[i + 1] = 0x17; buf[i + 2] = 0x16; buf[i + 3] = 0xFF;
                }
                else
                {
                    buf[i] = 0xFF; buf[i + 1] = 0xFF; buf[i + 2] = 0xFF; buf[i + 3] = 0xFF;
                }
            }
        }
        var bmp = new WriteableBitmap(dim, dim);
        using (var stream = bmp.PixelBuffer.AsStream())
        {
            stream.Write(buf, 0, buf.Length);
        }
        return bmp;
    }

    private void OnHotkeyPressed(int id)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (id)
            {
                case HotkeyPtt: TogglePtt(); break;
                case HotkeyMute: ToggleMicMute(); break;
                case HotkeyWake: ToggleAwake(); break;
            }
        });
    }

    /// <summary>(Re)apply the configured global hotkeys. Reports invalid specs and
    /// combos that are already held by another app, keeping prior bindings intact.</summary>
    internal void ApplyHotkeyBindings()
    {
        if (_hotkeys is null) return;
        foreach (var (id, spec) in new[]
                 {
                     (HotkeyPtt, _settings.HotkeyPtt ?? ""),
                     (HotkeyMute, _settings.HotkeyMute ?? ""),
                     (HotkeyWake, _settings.HotkeyWake ?? ""),
                 })
        {
            if (!HotkeyService.TryParse(spec, out var mods, out var vk))
            {
                _hotkeys.Unregister(id);
                Dbg($"hotkey {id}: invalid spec '{spec}' — not registered.");
                continue;
            }
            _hotkeys.Unregister(id);
            if (_hotkeys.Register(id, mods, vk))
                Dbg($"hotkey {id}: registered '{HotkeyService.ToDisplay(mods, vk)}'.");
            else
                Dbg($"hotkey {id}: '{spec}' is already in use by another app — not registered.");
        }
    }

    private float[] CaptureSnapshot()
    {
        List<float> snap;
        lock (_speechSeg) { snap = new List<float>(_speechSeg); _speechSeg.Clear(); }
        if (snap.Count > 0) return snap.ToArray();
        if (_captureSink is not null) { lock (_captureSink) { snap = new List<float>(_captureSink); _captureSink.Clear(); } }
        return snap?.ToArray() ?? [];
    }

    private void OnAudioSamples(ReadOnlySpan<float> pcm)
    {
        if (_micMuted) return;
        if (_voiceId.WakeEnrolled) _voiceId.Feed(pcm);
        lock (_verifyLock)
        {
            if (_verifyBuffer is not null)
                foreach (var s in pcm) _verifyBuffer.Add(s);
        }
        if (_captureSink is not null)
        {
            lock (_captureSink)
                foreach (var s in pcm) _captureSink.Add(s);
        }

        if (_vad is null || !_modelsLoaded) return;
        if (_sm.Current != AssistantState.Engaged) return;

        var chunk = ArrayPool<float>.Shared.Rent(512);
        try
        {
            foreach (var sample in pcm)
            {
                _vadBuf.Add(sample);
                while (_vadBuf.Count >= 512)
                {
                    _vadBuf.CopyTo(0, chunk, 0, 512);
                    _vadBuf.RemoveRange(0, 512);
                    var prob = _vad.PredictFrame(chunk.AsSpan(0, 512));

                    if (prob >= _vad.Threshold)
                    {
                        if (!_vadSpeaking)
                        {
                            _vadSpeaking = true;
                            _vadSilenceCount = 0;
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                OrbContainer.Fill = BrushFromHex("#66FFB703");
                                TickerText.Text = "SPEECH DETECTED";
                            });
                        }
                        _vadSilenceCount = 0;
                        lock (_speechSeg)
                            for (var i = 0; i < 512; i++) _speechSeg.Add(chunk[i]);
                    }
                    else
                    {
                        if (_vadSpeaking)
                        {
                            _vadSilenceCount++;
                            if (_vadSilenceCount >= VadSilenceFrames)
                            {
                                _vadSpeaking = false;
                                _vadSilenceCount = 0;
                                ProcessSpeechSegment();
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(chunk);
        }
    }

    private void ProcessSpeechSegment()
    {
        float[] pcm;
        lock (_speechSeg)
        {
            pcm = _speechSeg.ToArray();
            _speechSeg.Clear();
        }
        if (pcm.Length < 3200) return;
        _sttQueue.Enqueue(pcm);
        _ = PumpSttAsync();
    }

    private async Task PumpSttAsync()
    {
        if (_sttPumping) return;
        _sttPumping = true;
        try
        {
            while (_sttQueue.TryDequeue(out var pcm))
            {
                string? text = null;
                try
                {
                    text = await _whisper!.TranscribeAsync(pcm);
                }
                catch (Exception ex)
                {
                    DispatcherQueue.TryEnqueue(() => AddMessage("system", "STT error: " + ex.Message));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(text)) continue;
                var t = text;
                DispatcherQueue.TryEnqueue(() => { AddMessage("user", t); _ = RunVoiceCommand(t); });
            }
        }
        finally
        {
            _sttPumping = false;
            DispatcherQueue.TryEnqueue(() => ApplyStateVisual(_sm.Current));
        }
    }

    private async Task RunVoiceCommand(string text)
    {
        Dbg($"RunVoiceCommand: '{text}'");
        if (_geminiMode && _gemini.IsConnected)
        {
            try { await _gemini.SendTextAsync(text); }
            catch (Exception ex) { Dbg($"RunVoiceCommand Gemini: {ex.Message}"); }
            _sm.CommandFinished();
            return;
        }
        _sm.CommandStarted();
            var replyBox = AddMessage("ultron", "");
            try
            {
                var reply = await _brain.AskAsync(text, chunk =>
                {
                    if (replyBox.Children[1] is TextBlock tb)
                        DispatcherQueue.TryEnqueue(() => tb.Text += chunk);
                });
                Dbg($"RunVoiceCommand: reply='{reply}'");
                if (replyBox.Children[1] is TextBlock tb2 && string.IsNullOrEmpty(tb2.Text))
                    tb2.Text = reply;
                _sm.CommandFinished();
            }
        catch (Exception ex)
        {
            Dbg($"RunVoiceCommand: FAILED {ex.Message}");
            AddMessage("system", ex.Message);
            _sm.CommandFinished();
        }
    }

    private void OnAudioLevel(float level)
    {
        if (_audio is null || !_audio.IsActive) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_sm.Current != AssistantState.Sleep)
            {
                var scale = 1.0f + Math.Clamp(level * 3.0f, 0f, 0.35f);
                OrbPulse.ScaleX = OrbPulse.ScaleY = scale;
            }
            if (level > 0.15f && _sm.Current == AssistantState.Engaged)
                _sm.CommandStarted();
        });
    }

    /* ===================== VOICE ID ===================== */

    private void StartMonitor()
    {
        if (_audio is null) return;
        if (!_audio.IsActive)
        {
            var err = _audio.Start();
            if (err.Length > 0) AddMessage("system", err);
        }
    }

    private void StopMonitor()
    {
        if (_audio is not null && _audio.IsActive && _captureSink is null && _verifyBuffer is null)
            _audio.Stop();
    }

    private void OnWakeSpotted()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_geminiMode)
            {
                // Live mode: "Hey Ultron" heard on the local wake monitor —
                // flip the backend awake so Gemini starts hearing us.
                if (_gemini.IsConnected && _wakeGateOn && !_awakeInGemini)
                {
                    _lastInteraction = DateTime.UtcNow;
                    _awakeInGemini = true;
                    SetMicVisual(true);
                    AddMessage("system", "WAKE WORD — I'm listening.");
                    _ = _gemini.SetAwakeAsync(true);
                }
                return;
            }
            _lastInteraction = DateTime.UtcNow;
            if (_sm.Current is AssistantState.Sleep or AssistantState.Rest)
            {
                AddMessage("system", "WAKE WORD — verifying owner.");
                StartWakeVerify();
            }
        });
    }

    private void StartWakeVerify()
    {
        if (_geminiMode) return; // cannot capture mic for verify while Gemini streams it
        _sm.WakeDetected();
        _verifyBuffer = new List<float>();
        _verifyStart = DateTime.UtcNow;
        _silentTicks = 0;
        _verifyWatch.Start();
        SetMicVisual(true);
        SetConfidence(0f);
    }

    private void CheckVerifyProgress()
    {
        float[]? snap = null;
        lock (_verifyLock)
        {
            if (_verifyBuffer is null) return;
            snap = _verifyBuffer.ToArray();
        }
        var n = snap.Length;
        if (_sm.Current != AssistantState.WakeListening)
        {
            _verifyWatch.Stop();
            lock (_verifyLock) _verifyBuffer = null;
            SetMicVisual(false);
            return;
        }
        var tail = VoiceAudio.SampleRate / 5;
        if (n >= tail)
        {
            double sum = 0;
            for (var i = n - tail; i < n; i++)
            {
                var v = snap[i];
                sum += v * v;
            }
            if (Math.Sqrt(sum / tail) < 0.006) _silentTicks++;
            else _silentTicks = 0;
        }
        if (_silentTicks >= 3 || (DateTime.UtcNow - _verifyStart).TotalSeconds >= 4.0)
            FinalizeVerify();
    }

    private void FinalizeVerify()
    {
        _verifyWatch.Stop();
        float[] pcm;
        lock (_verifyLock)
        {
            pcm = (_verifyBuffer ?? new List<float>()).ToArray();
            _verifyBuffer = null;
        }
        SetMicVisual(false);
        if (_sm.Current != AssistantState.WakeListening) return;
        var score = _voiceId.LastOwnerScore(pcm);
        var ok = _voiceId.Verify(pcm);
        Dbg($"Verify: {pcm.Length} samples, score={score:F3}, threshold={_voiceId.OwnerThreshold:F2} -> {(ok ? "PASS" : "FAIL")}");
        AddMessage("system", $"VOICE CHECK: {(score * 100):F0}% — {(ok ? "OWNER VERIFIED" : "not recognized")}.");
        SetConfidence((float)score);
        if (ok) _sm.VoiceIdPassed();
        else _sm.VoiceIdFailed();
    }

    private void SetConfidence(float score)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var s = (float)Math.Clamp(score, 0f, 1f);
            var parent = ConfidenceFill.Parent as FrameworkElement;
            var w = Math.Max(2.0, (parent?.ActualWidth ?? 200.0) * s);
            ConfidenceFill.Width = w;
            ConfidenceFill.Background = BrushFromHex(s >= _voiceId.OwnerThreshold ? "#FFFFB703" : "#FFFF2E2E");
            ConfidenceText.Text = _voiceId.OwnerEnrolled
                ? $"{(s * 100):F0}%  (threshold {(s >= _voiceId.OwnerThreshold ? "MET" : "BELOW")} {_voiceId.OwnerThreshold:P0})"
                : "no voice profile enrolled";
            _dashboard?.PushConfidence(s);
        });
    }

    private async Task<float[]> CaptureUtteranceAsync(int ms)
    {
        var dest = new List<float>();
        var started = false;
        _captureSink = dest;
        if (_audio is not null && !_audio.IsActive)
        {
            var err = _audio.Start();
            if (err.Length > 0)
            {
                lock (dest) dest.Clear();
                _captureSink = null;
                return [];
            }
            started = true;
        }
        await Task.Delay(ms);
        _captureSink = null;
        if (started && _audio is not null) _audio.Stop();
        float[] result;
        lock (dest) result = dest.ToArray();
        return result;
    }

    private async Task OpenVoiceSetupAsync()
    {
        if (_settings.PinHash.Length == 0)
        {
            AddMessage("system", "Set a guard PIN in Settings first — it protects voice-ID reset.");
            return;
        }
        var status = new TextBlock
        {
            Text = "",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 155, 161, 171)),
            TextWrapping = TextWrapping.Wrap,
        };
        var wakeCount = _voiceId.WakeEnrolled ? _voiceId.WakeEnrolledCount() : 0;
        var ownerCount = _voiceId.OwnerEnrolled ? _voiceId.OwnerEnrolledCount() : 0;
        var recWake = new Button
        {
            Content = $"Record wake phrase  ({wakeCount}/2)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var recOwner = new Button
        {
            Content = $"Record passphrase  ({ownerCount}/3)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0),
        };
        recWake.Click += async (_, _) =>
        {
            recWake.IsEnabled = false;
            status.Text = "Saying \"Hey Ultron\" — recording 2.5s...";
            var idx = _voiceId.WakeEnrolledCount();
            var pcm = await CaptureUtteranceAsync(2500);
            if (pcm.Length == 0) { status.Text = "No audio captured."; }
            else
            {
                var score = _voiceId.AddWakeSample(pcm);
                status.Text = score < 0
                    ? "Unclear audio — repeat the wake phrase."
                    : $"Wake sample {idx + 1}/2 added (match {score:F2}).";
            }
            recWake.Content = $"Record wake phrase  ({_voiceId.WakeEnrolledCount()}/2)";
            recWake.IsEnabled = true;
        };
        recOwner.Click += async (_, _) =>
        {
            recOwner.IsEnabled = false;
            status.Text = "Saying your passphrase — recording 2.5s...";
            var idx = _voiceId.OwnerEnrolledCount();
            var pcm = await CaptureUtteranceAsync(2500);
            if (pcm.Length == 0) { status.Text = "No audio captured."; }
            else
            {
                var score = _voiceId.AddOwnerSample(pcm);
                status.Text = score < 0
                    ? "Unclear audio — repeat the passphrase."
                    : $"Passphrase sample {idx + 1}/3 added (match {score:F2}).";
            }
            recOwner.Content = $"Record passphrase  ({_voiceId.OwnerEnrolledCount()}/3)";
            recOwner.IsEnabled = true;
        };
        var pinBox = new PasswordBox { PlaceholderText = "Guard PIN (required to save/reset)" };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "VOICE ID SETUP",
            Content = new StackPanel
            {
                Spacing = 6,
                Width = 430,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Record the wake word \"Hey Ultron\" twice, then your passphrase " +
                               "\"Ultron, it's me\" three times. Speak naturally; keep 2m away from noise.",
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 155, 161, 171)),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    recWake,
                    recOwner,
                    status,
                    pinBox,
                },
            },
            PrimaryButtonText = "Save & Arm",
            CloseButtonText = "Cancel",
        };
        dialog.PrimaryButtonClick += (_, ea) =>
        {
            if (!_settings.VerifyPin(pinBox.Password))
            {
                status.Text = "Wrong guard PIN.";
                ea.Cancel = true;
                return;
            }
            if (!_voiceId.WakeEnrolled || !_voiceId.OwnerEnrolled)
            {
                status.Text = "Record at least 1 wake sample and 1 passphrase sample first.";
                ea.Cancel = true;
                return;
            }
            _settings.VoiceProfile = _voiceId.ToProfile();
            _settings.VoiceEnrolled = true;
            _settings.Save();
        };
        StopMonitor();
        await dialog.ShowAsync();
        if (_voiceId.WakeEnrolled)
        {
            StartMonitor();
            AddMessage("system", "VOICE ID armed — wake-word monitor active.");
        }
    }

    private void CommandInput_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) SendCommand();
    }

    private async void SendCommand()
    {
        var text = CommandInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        CommandInput.Text = "";
        _lastInteraction = DateTime.UtcNow;
        AddMessage("user", text);
        _sm.EngageFromText();

        if (_geminiMode && _gemini.IsConnected)
        {
            // Gemini Live path: send text; the spoken/text reply arrives async
            // via the TranscriptReceived event.
            try { await _gemini.SendTextAsync(text); }
            catch (Exception ex) { AddMessage("system", "Gemini: " + ex.Message); }
            return;
        }

        var replyBox = AddMessage("ultron", "");
        try
        {
            var reply = await _brain.AskAsync(text, chunk =>
            {
                if (replyBox.Children[1] is TextBlock tb)
                {
                    DispatcherQueue.TryEnqueue(() => tb.Text += chunk);
                }
            });
            if (replyBox.Children[1] is TextBlock tb2 && string.IsNullOrEmpty(tb2.Text))
            {
                tb2.Text = reply;
            }
            _sm.CommandFinished();
        }
        catch (Exception ex)
        {
            AddMessage("system", ex.Message);
            _sm.CommandFinished();
        }
    }

    /* ===================== QUICK ACTIONS ===================== */

    private void QuickAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        _lastInteraction = DateTime.UtcNow;
        switch (btn.Tag)
        {
            case "purge":
                _brain.ResetHistory();
                ConversationList.Items.Clear();
                AddMessage("system", "Purge Cache: history cleared.");
                break;
            case "wake":
                if (_voiceId.OwnerEnrolled && _settings.VoiceEnrolled)
                {
                    AddMessage("system", "Wake simulated — verifying owner.");
                    StartWakeVerify();
                }
                else
                {
                    AddMessage("system", "No voice profile yet — opening VOICE ID SETUP.");
                    _ = OpenVoiceSetupAsync();
                }
                break;
            case "lock":
                _sm.ForceSleep();
                AddMessage("system", "System locked — Ultron asleep.");
                break;
            case "focus":
                if (_sm.Current == AssistantState.Engaged) _sm.RestCue();
                else _sm.ForceSleep();
                AddMessage("system", "Focus mode — Ultron resting until next command.");
                break;
            case "settings":
                _ = OpenSettingsAsync();
                break;
            default:
                AddMessage("system", $"Quick action: {btn.Tag} (handler pending).");
                break;
        }
    }

    /* ===================== TELEMETRY ===================== */

    private void StartTelemetryLoop()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) =>
        {
            var s = _telemetry.Snapshot();
            MemText.Text = $"MEM  {s.MemMb,6:F0} MB";
            CpuText.Text = $"CPU  {s.CpuPercent,4:F0}%";
            ThreadText.Text = $"THR  {s.ThreadCount,4}";
            CamText.Text = $"UP   {(int)s.Uptime.TotalHours}h {s.Uptime.Minutes}m";

            _spark.Enqueue(Math.Clamp(s.CpuPercent, 0, 100));
            if (_spark.Count > 30) _spark.Dequeue();
            RenderSparkline(_spark.ToArray());
        };
        t.Start();
    }

    private void RenderSparkline(double[] values)
    {
        var n = values.Length;
        if (n < 2) return;
        var max = 100.0;
        var w = 84.0;
        var h = 30.0;
        var points = new Microsoft.UI.Xaml.Media.PointCollection();
        for (var i = 0; i < n; i++)
        {
            var x = (i / (double)(n - 1)) * w;
            var y = h - (values[i] / max) * (h - 4) - 2;
            points.Add(new Windows.Foundation.Point(x, y));
        }
        SparkLine.Points = points;
    }

    /* ===================== ORB ===================== */

    private void StartOrbSpin()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        var angle = 0.0;
        t.Tick += (_, _) =>
        {
            angle = (angle + 1.2) % 360;
            OrbSpin.Angle = angle;
        };
        t.Start();
    }

    /* ===================== STATE MACHINE HUD ===================== */

    private void OnStateChanged(StateTransition t)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyStateVisual(t.To);
            LogEnqueued("system", $"State {t.From} → {t.To} ({t.Reason}).");
        });
    }

    private void ApplyStateVisual(AssistantState st)
    {
        var (fill, stroke, label, dot, ticker) = st switch
        {
            AssistantState.Sleep => ("#1AFF2E2E", "#66FF2E2E", "STANDBY", "#FF9BA1AB", "CORE STATUS: STANDBY"),
            AssistantState.WakeListening => ("#33FFB703", "#FFFFB703", "WAKE LISTEN", "#FFFFB703", "CORE STATUS: WAKE LISTEN"),
            AssistantState.Engaged => ("#66FF2E2E", "#FFFF2E2E", "ENGAGED", "#FFFF2E2E", "CORE STATUS: ENGAGED"),
            AssistantState.Rest => ("#22FF2E2E", "#AAFF2E2E", "REST", "#AAFF2E2E", "CORE STATUS: REST"),
            _ => ("#1AFF2E2E", "#66FF2E2E", "STANDBY", "#FF9BA1AB", "CORE STATUS: STANDBY"),
        };
        OrbContainer.Fill = BrushFromHex(fill);
        OrbContainer.Stroke = BrushFromHex(stroke);
        OrbStateLabel.Text = label;
        TickerDot.Fill = BrushFromHex(dot);
        TickerText.Text = ticker;
    }

    private void SetMicVisual(bool busy)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            MicButton.Foreground = busy
                ? BrushFromHex("#FFFF2E2E")
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 201, 206, 214));
        });
    }

    private void LogEnqueued(string who, string text)
    {
        DispatcherQueue.TryEnqueue(() => AddMessage(who, text));
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        var s = hex.TrimStart('#');
        return new SolidColorBrush(Color.FromArgb(
            byte.Parse(s[..2], NumberStyles.HexNumber),
            byte.Parse(s[2..4], NumberStyles.HexNumber),
            byte.Parse(s[4..6], NumberStyles.HexNumber),
            byte.Parse(s[6..8], NumberStyles.HexNumber)));
    }

    /* ===================== GEMINI BACKEND ===================== */

    private void OnGeminiStatusChanged(string state)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (state)
            {
                case "connected":
                    _geminiMode = true;
                    // Apply the persisted Ultron voice profile on every connect.
                    _ = _gemini.SendVoiceDspAsync(
                        _settings.VoiceDspEnabled,
                        _settings.VoiceDspPitch,
                        _settings.VoiceDspChorus,
                        _settings.VoiceDspBass,
                        _settings.VoiceDspDarken);
                    // Wake gate: active only when the user enrolled a wake
                    // phrase AND the setting is on. If armed, start asleep and
                    // keep the local mic running purely as the "Hey Ultron"
                    // detector; the Python backend mutes itself while asleep.
                    _wakeGateOn = _settings.WakeWordEnabled && _voiceId.WakeEnrolled;
                    _awakeInGemini = !_wakeGateOn;
                    _ = _gemini.SetWakeEnabledAsync(_wakeGateOn);
                    if (_wakeGateOn)
                    {
                        TickerText.Text = "CORE STATUS: ASLEEP — say \"Hey Ultron\"";
                        AddMessage("system", "Wake gate armed — say \"Hey Ultron\" to talk to me.");
                        SetMicVisual(false);
                        StartMonitor();
                        _ = _gemini.SetAwakeAsync(false);
                    }
                    else
                    {
                        TickerText.Text = "CORE STATUS: GEMINI LIVE CONNECTED";
                        AddMessage("system", "Gemini Live API connected — real-time voice active.");
                        StopLocalMic();
                        _ = _gemini.SetAwakeAsync(true);
                    }
                    break;
                case "connecting":
                    TickerText.Text = "CORE STATUS: CONNECTING TO GEMINI...";
                    break;
                case "awake":
                    _awakeInGemini = true;
                    TickerText.Text = "CORE STATUS: LISTENING";
                    SetMicVisual(true);
                    break;
                case "asleep":
                    _awakeInGemini = false;
                    TickerText.Text = "CORE STATUS: ASLEEP — say \"Hey Ultron\"";
                    SetMicVisual(false);
                    break;
                case "listening":
                    TickerText.Text = "CORE STATUS: LISTENING";
                    break;
                case "speaking":
                    TickerText.Text = "CORE STATUS: SPEAKING";
                    break;
                case "disconnected":
                    _geminiMode = false;
                    _awakeInGemini = true;
                    _wakeGateOn = false;
                    SetMicVisual(false);
                    TickerText.Text = "CORE STATUS: GEMINI DISCONNECTED";
                    // Fall back to local STT/TTS pipeline (loads models lazily if skipped).
                    _ = EnsureLocalModels();
                    ResumeLocalMic();
                    break;
            }
            _dashboard?.PushStatus(state, _awakeInGemini);
        });
    }

    private void StopLocalMic()
    {
        try
        {
            _audio?.Stop();
            _verifyWatch.Stop();
            lock (_verifyLock) _verifyBuffer = null;
        }
        catch (Exception ex) { AppLog.Write("MainWindow", $"StopLocalMic failed: {ex.Message}", AppLog.Level.Warn); }
    }

    private void ResumeLocalMic()
    {
        try
        {
            if (_voiceId.WakeEnrolled)
                StartMonitor();
        }
        catch (Exception ex) { AppLog.Write("MainWindow", $"ResumeLocalMic failed: {ex.Message}", AppLog.Level.Warn); }
    }

    private void OnGeminiTranscript(string role, string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AddMessage(role == "user" ? "user" : "ultron", text);
            _dashboard?.PushTranscript(role == "user" ? "user" : "ultron", text);
            if (role == "user")
            {
                _lastInteraction = DateTime.UtcNow;
                _sm.EngageFromText();
            }
            else
            {
                _sm.CommandFinished();
            }
        });
    }

    private void OnGeminiToolCall(ToolCall call)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            Dbg($"Gemini tool call: {call.Name}");
            // The router owns policy, approval and failure translation, so this
            // method only has to hand the call over and report the outcome.
            var result = await _toolRouter.ExecuteAsync(call);
            try
            {
                await _gemini.SendToolResultAsync(call.Id, call.Name, result);
            }
            catch (Exception ex)
            {
                Dbg($"Could not deliver result for {call.Name}: {ex.Message}");
            }
        });
    }

    private void OnComputerActRequested(string id, JsonElement action)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (!await RequestComputerActionApproval(action))
                {
                    await _gemini.SendComputerActResultAsync(id, "User denied the desktop action.");
                    return;
                }
                var result = ExecuteComputerAct(action);
                await _gemini.SendComputerActResultAsync(id, result);
            }
            catch (Exception ex)
            {
                await _gemini.SendComputerActResultAsync(id, $"Action failed: {ex.Message}");
            }
        });
    }

    private async Task<bool> RequestComputerActionApproval(JsonElement action)
    {
        await _approvalGate.WaitAsync();
        try
        {
            var description = action.GetRawText();
            if (description.Length > 600) description = description[..600] + "…";
            var dialog = new ContentDialog
            {
                XamlRoot = Content?.XamlRoot,
                Title = "DESKTOP ACTION APPROVAL REQUIRED",
                Content = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Approve",
                CloseButtonText = "Deny",
            };
            if (dialog.XamlRoot == null) return false;
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _approvalGate.Release();
        }
    }

    private string ExecuteComputerAct(JsonElement action)
    {
        var type = (action.TryGetProperty("type", out var t) ? t.GetString() : "")?.ToLowerInvariant() ?? "";
        var x = action.TryGetProperty("x", out var xe) && xe.ValueKind == JsonValueKind.Number ? (int?)xe.GetInt32() : null;
        var y = action.TryGetProperty("y", out var ye) && ye.ValueKind == JsonValueKind.Number ? (int?)ye.GetInt32() : null;
        var amount = action.TryGetProperty("amount", out var ae) && ae.ValueKind == JsonValueKind.Number ? ae.GetInt32() : 1;
        if ((x < 0 || y < 0 || x > 10000 || y > 10000) && type is "click" or "double_click" or "right_click" or "move")
            return "Desktop coordinates are outside the allowed range.";
        if (type == "scroll" && Math.Abs(amount) > 100)
            return "Scroll amount is outside the allowed range.";

        switch (type)
        {
            case "click":
                ClickAt(x, y);
                return $"Clicked at ({x ?? CurrentX()}, {y ?? CurrentY()}).";
            case "double_click":
                ClickAt(x, y, doubleClick: true);
                return $"Double-clicked at ({x ?? CurrentX()}, {y ?? CurrentY()}).";
            case "right_click":
                RightClickAt(x, y);
                return $"Right-clicked at ({x ?? CurrentX()}, {y ?? CurrentY()}).";
            case "move":
                MoveMouse(x, y);
                return $"Moved to ({x ?? CurrentX()}, {y ?? CurrentY()}).";
            case "scroll":
                Scroll(amount);
                return $"Scrolled {amount} notch(es).";
            case "type":
            case "type_text":
                var text = action.TryGetProperty("text", out var te) ? te.GetString() ?? "" : "";
                SendTypedText(text);
                return $"Typed: {text}";
            case "press":
            case "press_key":
                var keys = action.TryGetProperty("keys", out var ke) ? ke.GetString() ?? "" : "";
                var args = new Dictionary<string, object> { ["keys"] = keys };
                return HandlePressKey(args);
            default:
                return $"Unknown computer action type '{type}'.";
        }
    }

    private void OnGeminiError(string message)
    {
        DispatcherQueue.TryEnqueue(() => AddMessage("system", $"Gemini: {message}"));
    }

    private void ScheduleShutdown(int afterSeconds)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(afterSeconds * 1000);
            DispatcherQueue.TryEnqueue(() => Application.Current.Exit());
        });
    }

    private void OnGeminiDisconnected()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AddMessage("system", "Gemini disconnected — attempting reconnect in 5s...");
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                if (!string.IsNullOrEmpty(_settings.GeminiApiKey))
                {
                    await _gemini.StartAsync(_settings.GeminiApiKey, _settings.GeminiVoice);
                    if (_micMuted) await _gemini.SetMicMutedAsync(true);
                }
            });
        });
    }

    private void HandleCallStateChanged(string state, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (CallStatusOverlay is null) return;

            switch (state.ToLowerInvariant())
            {
                case "ringing":
                    CallStatusOverlay.Visibility = Visibility.Visible;
                    CallStatusText.Text = "RINGING";
                    CallTargetText.Text = message;
                    CallDurationText.Text = "00:00";
                    _callDuration = TimeSpan.Zero;
                    CallStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Orange);
                    CallHangupButton.Visibility = Visibility.Visible;
                    CallAcceptButton.Visibility = Visibility.Collapsed;
                    CallDeclineButton.Visibility = Visibility.Collapsed;
                    _callTimer?.Start();
                    break;

                case "connecting":
                    CallStatusText.Text = "CONNECTING";
                    CallTargetText.Text = message;
                    CallStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Orange);
                    break;

                case "connected":
                    CallStatusText.Text = "CONNECTED";
                    CallTargetText.Text = message;
                    CallStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
                    CallAcceptButton.Visibility = Visibility.Collapsed;
                    CallDeclineButton.Visibility = Visibility.Collapsed;
                    break;

                case "remote_speech":
                    if (message == "started")
                        CallStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Cyan);
                    else
                        CallStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
                    break;

                case "ended":
                case "error":
                    CallStatusText.Text = state.ToUpperInvariant();
                    CallTargetText.Text = message;
                    CallStatusDot.Fill = new SolidColorBrush(state == "error" ? Microsoft.UI.Colors.Red : Microsoft.UI.Colors.Gray);
                    CallHangupButton.Visibility = Visibility.Collapsed;
                    _callTimer?.Stop();
                    // Auto-hide after 5 seconds
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5000);
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (CallStatusOverlay is not null)
                                CallStatusOverlay.Visibility = Visibility.Collapsed;
                        });
                    });
                    break;
            }
        });
    }

    private void UpdateCallDuration()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _callDuration = _callDuration.Add(TimeSpan.FromSeconds(1));
            var mins = _callDuration.Minutes;
            var secs = _callDuration.Seconds;
            if (CallDurationText is not null)
                CallDurationText.Text = $"{mins:D2}:{secs:D2}";
        });
    }

    private void CallHangupButton_Click(object sender, RoutedEventArgs e)
    {
        _ = _gemini.SendTelegramCallStopAsync();
    }

    private void CallAcceptButton_Click(object sender, RoutedEventArgs e)
    {
        // For incoming calls (not implemented yet - we only do outgoing)
    }

    private void CallDeclineButton_Click(object sender, RoutedEventArgs e)
    {
        _ = _gemini.SendTelegramCallStopAsync();
    }


    /// <summary>
    /// Flattens typed arguments for handlers that have not been migrated yet.
    /// Mirrors the old JsonElement.ToString() behaviour so no handler changes
    /// meaning while it is being moved into a tool service.
    /// </summary>
    private static Dictionary<string, object> ToLegacyArgs(JsonElement arguments)
    {
        var legacy = new Dictionary<string, object>();
        foreach (var prop in arguments.EnumerateObject())
            legacy[prop.Name] = prop.Value.ToString();
        return legacy;
    }

    private string Shutdown()
    {
        ScheduleShutdown(4);
        return "Understood. Shutting down now.";
    }

    /// <summary>
    /// Finds a freshly launched app's window, focuses it and makes it the typing
    /// target, so "open X, then type Y" behaves as a user expects.
    ///
    /// Win32 window handles stay in the UI layer, but the wait is awaited rather
    /// than slept: tool calls are dispatched onto the UI thread, so blocking here
    /// froze the whole window for up to four seconds while the app started.
    /// </summary>
    async Task<IntPtr> ILaunchedWindowActivator.ActivateAsync(
        string imageName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var hwnd = Native.FindMainWindow(imageName);
            if (hwnd != IntPtr.Zero)
            {
                _lastTargetWindow = hwnd;
                Native.FocusWindow(hwnd);
                return hwnd;
            }
            await Task.Delay(200, cancellationToken).ConfigureAwait(true);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Shows the approval dialog. This is the only place the tool layer touches
    /// the UI, and it is reached through <see cref="IApprovalService"/> - no tool
    /// service ever holds a reference to this window.
    /// </summary>
    async Task<bool> IApprovalService.RequestApprovalAsync(
        ToolCall toolCall, PolicyDecision decision, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;

        // One dialog at a time: overlapping prompts would race the user's answer.
        await _approvalGate.WaitAsync(cancellationToken);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content?.XamlRoot,
                Title = decision.Risk >= RiskLevel.High
                    ? "HIGH-RISK ACTION NEEDS APPROVAL"
                    : "TOOL APPROVAL REQUIRED",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = $"ULTRON wants to execute: {toolCall.Name}", TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new TextBlock { Text = decision.Summary, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 155, 161, 171)) },
                        new TextBlock { Text = $"Risk: {decision.Risk}", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 120, 128, 140)) },
                    }
                },
                PrimaryButtonText = "Approve",
                CloseButtonText = "Deny",
            };

            if (dialog.XamlRoot == null) return false;
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _approvalGate.Release();
        }
    }

    private readonly SemaphoreSlim _approvalGate = new(1, 1);

    /* ===================== UNDO LEDGER ===================== */

    // The ledger itself lives in Services/UndoLedger so the file and app services
    // can register reversals without knowing about this window.
    private void PushUndo(string desc, Func<string> undo) => _undo.Push(desc, undo);

    private string UndoHistory() => _undo.History();

    private string HandleUndo()
    {
        var result = _undo.Undo();
        Dbg($"Undo: {result}");
        return result;
    }

    private string HandleSaveMemory(Dictionary<string, object> args)
    {
        var category = args.GetValueOrDefault("category")?.ToString() ?? "notes";
        var key = args.GetValueOrDefault("key")?.ToString() ?? "";
        var value = args.GetValueOrDefault("value")?.ToString() ?? "";
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
            return "Missing key or value.";
        
        // Store in SQLite MemoryStore as a fact with topic = category
        var factText = $"[{category}] {key}: {value}";
        _ = Task.Run(async () =>
        {
            try { await _memory.AddFactAsync(factText, category); }
            catch (Exception ex) { AppLog.Write("MainWindow", $"AddFactAsync failed: {ex.Message}", AppLog.Level.Error); }
        });
        
        PushUndo($"save_memory([{category}] {key})", () =>
        {
            // Note: MemoryStore doesn't support fact removal by key, so we can't fully undo
            return "Fact stored in memory (manual removal needed if desired).";
        });
        
        if (_memCache.Count > 0) RefreshMemory();
        return $"Remembered [{category}] {key}: {value}";
    }

    private string HandleRecallMemory(Dictionary<string, object> args)
    {
        var query = args.GetValueOrDefault("query")?.ToString() ?? "";
        
        // Search both facts and conversations in SQLite MemoryStore
        var results = new List<string>();
        
        // Search facts
        var factsTask = Task.Run(async () =>
        {
            try { return await _memory.ListFactsAsync(); }
            catch { return new List<string>(); }
        });
        
        // Search conversations
        var convsTask = Task.Run(async () =>
        {
            try { return await _memory.SearchConversationsAsync(query, 6); }
            catch { return new List<ConversationRow>(); }
        });
        
        Task.WaitAll(factsTask, convsTask);
        
        var facts = factsTask.Result;
        var convs = convsTask.Result;
        
        var q = query.ToLowerInvariant();
        foreach (var f in facts)
        {
            if (q.Length == 0 || f.ToLowerInvariant().Contains(q))
                results.Add($"[fact] {f}");
        }
        foreach (var c in convs)
        {
            if (q.Length == 0 || c.Text.ToLowerInvariant().Contains(q))
                results.Add($"[conv] {c.Ts} {c.Role}: {c.Text}");
        }
        
        return results.Count > 0 ? string.Join("\n", results.Take(8)) : "Nothing found in memory.";
    }

    private async Task<string> HandleTypeTextAsync(Dictionary<string, object> args)
    {
        var text = args.GetValueOrDefault("text")?.ToString() ?? "";
        if (string.IsNullOrEmpty(text)) return "No text supplied.";
        // Resolve where the user actually wants the text typed.
        var target = ResolveTypeTarget();
        if (target == IntPtr.Zero)
            return "No active window found to type into — click a window first, then try again.";
        try
        {
            Native.FocusWindow(target);
            await System.Threading.Tasks.Task.Delay(250);
            SendTypedText(text);
            var targetName = Native.WindowTitle(target);
            PushUndo("type_text", () =>
            {
                try
                {
                    // Ctrl+Z to undo the typed text in the focused app.
                    var inputs = new List<NativeInput>()
                    {
                        KeyCodeInput(0x11, false, unicode: false), // Ctrl down
                        KeyCodeInput(0x5A, false, unicode: false), // Z down
                        KeyCodeInput(0x5A, true, unicode: false),  // Z up
                        KeyCodeInput(0x11, true, unicode: false),  // Ctrl up
                    };
                    SendInputBatch(inputs);
                    return "Sent Ctrl+Z (undo).";
                }
                catch (Exception ex) { return $"Ctrl+Z failed: {ex.Message}"; }
            });
            return $"Typed: {text}" + (string.IsNullOrEmpty(targetName) ? "" : $" (into \"{targetName}\")");
        }
        catch (Exception ex) { return $"Could not type: {ex.Message}"; }
    }

    private static readonly Dictionary<string, ushort> _vkMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["space"] = 0x20,
        ["backspace"] = 0x08, ["back"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E,
        ["insert"] = 0x2D, ["escape"] = 0x1B, ["esc"] = 0x1B,
        ["ctrl"] = 0x11, ["control"] = 0x11, ["alt"] = 0x12, ["shift"] = 0x10,
        ["win"] = 0x5B, ["windows"] = 0x5B, ["lwin"] = 0x5B, ["rwin"] = 0x5C,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["capslock"] = 0x14, ["numlock"] = 0x90, ["scrolllock"] = 0x91,
        ["printscreen"] = 0x2C, ["pause"] = 0x13, ["menu"] = 0x5D,
        ["play"] = 0xB3, ["media_play_pause"] = 0xB3,
        ["next"] = 0xB0, ["media_next"] = 0xB0, ["next_track"] = 0xB0,
        ["prev"] = 0xB1, ["previous"] = 0xB1, ["media_prev"] = 0xB1,
        ["stop"] = 0xB2, ["media_stop"] = 0xB2,
        ["volume_up"] = 0xAF, ["volume_down"] = 0xAE, ["mute"] = 0xAD,
        ["volume_mute"] = 0xAD, ["mymusic"] = 0xB4, ["launchmail"] = 0xB4,
        ["semicolon"] = 0xBA, ["quot"] = 0xDE, ["quote"] = 0xDE, ["tick"] = 0xC0,
        ["minus"] = 0xBD, ["equals"] = 0xBB, ["comma"] = 0xBC, ["period"] = 0xBE,
        ["slash"] = 0xBF, ["backslash"] = 0xDC, ["lbracket"] = 0xDB, ["rbracket"] = 0xDD,
        ["numpad_0"] = 0x60, ["numpad_1"] = 0x61, ["numpad_2"] = 0x62, ["numpad_3"] = 0x63,
        ["numpad_4"] = 0x64, ["numpad_5"] = 0x65, ["numpad_6"] = 0x66, ["numpad_7"] = 0x67,
        ["numpad_8"] = 0x68, ["numpad_9"] = 0x69, ["decimal"] = 0x6E, ["add"] = 0x6B,
        ["subtract"] = 0x6D, ["multiply"] = 0x6A, ["divide"] = 0x6F,
        ["f1"] = 0x70, ["f2"] = 0x71, ["f3"] = 0x72, ["f4"] = 0x73, ["f5"] = 0x74,
        ["f6"] = 0x75, ["f7"] = 0x76, ["f8"] = 0x77, ["f9"] = 0x78, ["f10"] = 0x79,
        ["f11"] = 0x7A, ["f12"] = 0x7B, ["f13"] = 0x7C, ["f14"] = 0x7D, ["f15"] = 0x7E,
        ["f16"] = 0x7F, ["f17"] = 0x80, ["f18"] = 0x81, ["f19"] = 0x82, ["f20"] = 0x83,
        ["f21"] = 0x84, ["f22"] = 0x85, ["f23"] = 0x86, ["f24"] = 0x87,
    };

    private static ushort LookupVk(string token)
    {
        var t = token.Trim().ToLowerInvariant();
        if (_vkMap.TryGetValue(t, out var vk)) return vk;
        if (t.Length == 1)
        {
            char c = t[0];
            if (c >= 'a' && c <= 'z') return (ushort)(c - 'a' + 0x41);
            if (c >= 'A' && c <= 'Z') return (ushort)(c - 'A' + 0x41);
            if (c >= '0' && c <= '9') return (ushort)(c - '0' + 0x30);
        }
        return 0;
    }

    private string HandlePressKey(Dictionary<string, object> args)
    {
        var spec = args.GetValueOrDefault("keys")?.ToString()
                   ?? args.GetValueOrDefault("key")?.ToString() ?? "";
        var repeat = args.GetValueOrDefault("repeat") is JsonElement je && je.TryGetInt32(out var rr) ? rr : 1;
        if (string.IsNullOrEmpty(spec)) return "No key given.";
        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "No key given.";
        var codes = parts.Select(LookupVk).ToList();
        var bad = parts.Zip(codes, (p, c) => c == 0 ? p : null).FirstOrDefault(n => n != null);
        if (bad != null) return $"Unrecognized key: {bad}. Supported: letters, digits, enter, tab, backspace, delete, escape, arrows, f1-f12, ctrl/alt/shift/win combos.";
        if (repeat < 1) repeat = 1;
        if (repeat > 50) repeat = 50;
        try
        {
            var main = codes[^1];
            var mods = codes.Take(codes.Count - 1).ToList();
            for (int r = 0; r < repeat; r++)
            {
                var inputs = new List<NativeInput>();
                foreach (var m in mods) inputs.Add(KeyCodeInput(m, false, unicode: false));
                inputs.Add(KeyCodeInput(main, false, unicode: false));
                inputs.Add(KeyCodeInput(main, true, unicode: false));
                foreach (var m in ((IEnumerable<ushort>)mods).Reverse()) inputs.Add(KeyCodeInput(m, true, unicode: false));
                SendInputBatch(inputs);
            }
            PushUndo($"press_key({spec})", () =>
            {
                // Re-press the same shortcut to toggle/appose where sensible (backspace, delete,
                // enter) — otherwise just report the press for the transcript.
                return $"Pressed {spec} (repeat {(repeat > 1 ? repeat : 1)}).";
            });
            return $"Pressed {spec}" + (repeat > 1 ? $" x{repeat}" : "") + ".";
        }
        catch (Exception ex) { return $"Could not press {spec}: {ex.Message}"; }
    }

    private void TrackActiveWindow()
    {
        try
        {
            var fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == _selfHwnd) return;
            if (Native.GetWindowThreadProcessId(fg, out uint pid) == 0) return;
            if (pid == 0 || pid == (uint)Environment.ProcessId) return;
            var title = Native.WindowTitle(fg);
            if (string.IsNullOrWhiteSpace(title)) return;
            var r = Native.GetWindowRectR(fg);
            if (r.Right - r.Left < 60 || r.Bottom - r.Top < 40) return;
            _lastTargetWindow = fg;
        }
        catch { }
    }

    internal IntPtr ResolveTypeTarget()
    {
        try
        {
            var fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != _selfHwnd)
            {
                if (Native.GetWindowThreadProcessId(fg, out uint pid) != 0 && pid != 0 &&
                    pid != (uint)Environment.ProcessId &&
                    !string.IsNullOrWhiteSpace(Native.WindowTitle(fg)))
                {
                    return fg;
                }
            }
            if (_lastTargetWindow != IntPtr.Zero && Native.IsWindow(_lastTargetWindow))
                return _lastTargetWindow;
        }
        catch { }
        return IntPtr.Zero;
    }

    internal static void SendTypedText(string text)
    {
        var inputs = new List<NativeInput>();
        foreach (char c in text)
        {
            if (c == '\r' || c == '\n')
            {
                inputs.Add(KeyCodeInput(0x0D, false, unicode: false)); // Enter
                inputs.Add(KeyCodeInput(0x0D, true, unicode: false));
            }
            else if (c == '\t')
            {
                inputs.Add(KeyCodeInput(0x09, false, unicode: false)); // Tab
                inputs.Add(KeyCodeInput(0x09, true, unicode: false));
            }
            else
            {
                inputs.Add(KeyCodeInput((ushort)c, false, unicode: true));
                inputs.Add(KeyCodeInput((ushort)c, true, unicode: true));
            }
        }
        SendInputBatch(inputs);
    }

    internal static void SendInputBatch(List<NativeInput> inputs)
    {
        if (inputs.Count == 0) return;
        var arr = inputs.ToArray();
        var sent = 0;
        for (int i = 0; i < arr.Length;)
        {
            var n = Math.Min(32, arr.Length - i);
            var chunk = arr[i..(i + n)];
            var r = Native.SendInput((uint)chunk.Length, chunk, NativeInput.Size);
            if (r == 0)
            {
                var err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"SendInput rejected ({err}).");
            }
            sent += (int)r;
            i += n;
        }
        Dbg($"SendInput: {sent}/{inputs.Count} events injected.");
    }

    internal static NativeInput KeyCodeInput(ushort code, bool keyUp, bool unicode)
    {
        return new NativeInput
        {
            Type = 1, // INPUT_KEYBOARD
            kb = new NativeKeyboardInput
            {
                wVk = unicode ? (ushort)0 : code,
                wScan = unicode ? code : (ushort)0,
                dwFlags = (uint)((keyUp ? Native.KEYEVENTF_KEYUP : 0) |
                                 (unicode ? Native.KEYEVENTF_UNICODE : 0)),
            },
        };
    }

    private string HandleSystemStatus()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var mem = proc.WorkingSet64 / 1024 / 1024;
        var totalThreads = 0;
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try { totalThreads += p.Threads.Count; } catch { }
        }
        return $"Memory: {mem} MB, Threads across system: {totalThreads}, Uptime: {DateTime.Now - proc.StartTime:hh\\:mm\\:ss}";
    }

    private string HandleComputerSettings(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
        switch (action.ToLowerInvariant())
        {
            case "volume_up":
            case "volume_down":
            case "mute":
            case "unmute":
            {
                var before = SystemVolume.Get();
                string result;
                switch (action.ToLowerInvariant())
                {
                    case "volume_up": result = RunCommand("nircmd.exe", "changesysvolume 2000"); break;
                    case "volume_down": result = RunCommand("nircmd.exe", "changesysvolume -2000"); break;
                    case "mute": result = RunCommand("nircmd.exe", "mutesysvolume 1"); break;
                    default: result = RunCommand("nircmd.exe", "mutesysvolume 0"); break;
                }
                // Undo restores the EXACT prior volume + mute state. If we could
                // not read the value, register nothing — undoing a guess is worse.
                if (before.volume >= 0f)
                {
var (v, m) = before;
                    PushUndo($"computer_settings({action})", () =>
                    {
                        SystemVolume.Set(v, m);
                        var pct = (int)(v * 100f);
                        var suffix = m ? " (muted)" : "";
                        return $"Restored volume to {pct}%{suffix}.";
                    });
                }
                return result;
            }
            case "shutdown": return RunCommand("shutdown", "/s /t 30");
            case "restart": return RunCommand("shutdown", "/r /t 30");
            case "sleep": return RunCommand("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
            case "lock": return RunCommand("rundll32.exe", "user32.dll,LockWorkStation");
            case "screenshot": return RunCommand("snippingtool", "/clip");
            default: return $"Unknown action: {action}";
        }
    }

    private static string RunCommand(string file, string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(file, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return $"Failed: could not start {file}";
            p.WaitForExit(30000); // 30 second timeout
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            var output = string.IsNullOrEmpty(stdout) ? stderr : stdout;
            return $"Exit code: {p.ExitCode}. {(string.IsNullOrEmpty(output) ? "No output." : output.Trim())}";
        }
        catch (Exception ex) { return $"Failed: {ex.Message}"; }
    }

    private async Task<string> HandleWebSearchAsync(Dictionary<string, object> args)
    {
        var query = args.GetValueOrDefault("query")?.ToString() ?? "";
        if (string.IsNullOrEmpty(query)) return "No query supplied.";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
            var html = await http.GetStringAsync(url);
            // Extract text snippets from DDG HTML
            var results = new List<string>();
            var idx = 0;
            while (idx < html.Length && results.Count < 5)
            {
                var start = html.IndexOf("result__snippet", idx, StringComparison.Ordinal);
                if (start < 0) break;
                var linkStart = html.IndexOf('>', start);
                var linkEnd = html.IndexOf("</a>", linkStart, StringComparison.Ordinal);
                if (linkEnd < 0) break;
                var snippet = System.Net.WebUtility.HtmlDecode(html[(linkStart + 1)..linkEnd]).Trim();
                if (!string.IsNullOrEmpty(snippet))
                    results.Add(snippet);
                idx = linkEnd + 4;
            }
            return results.Count > 0
                ? $"Search results for \"{query}\":\n" + string.Join("\n", results.Select((r, i) => $"{i + 1}. {r}"))
                : $"No results found for \"{query}\".";
        }
        catch (Exception ex) { return $"Search failed: {ex.Message}"; }
    }

    private async Task<string> HandleWeatherAsync(Dictionary<string, object> args)
    {
        var city = args.GetValueOrDefault("city")?.ToString() ?? "";
        if (string.IsNullOrEmpty(city)) return "No city supplied.";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var url = $"https://wttr.in/{Uri.EscapeDataString(city)}?format=3";
            var result = await http.GetStringAsync(url);
            return result.Trim();
        }
        catch (Exception ex) { return $"Weather lookup failed: {ex.Message}"; }
    }

    private string HandleReminder(Dictionary<string, object> args)
    {
        var date = args.GetValueOrDefault("date")?.ToString() ?? "";
        var time = args.GetValueOrDefault("time")?.ToString() ?? "";
        var message = args.GetValueOrDefault("message")?.ToString() ?? "";
        // Reminder persistence is not yet implemented; log it for now.
        var logEntry = $"[Reminder] {date} {time} — {message}";
        _ = Task.Run(async () =>
        {
            try { await _memory.LogAsync("system", logEntry); }
            catch { }
        });
        return $"Reminder noted: {date} {time} — {message}. (Reminder notifications not yet implemented; logged locally.)";
    }

    private async Task<string> HandleBrowserControlAsync(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
        var url = args.GetValueOrDefault("url")?.ToString() ?? "";
        var query = args.GetValueOrDefault("query")?.ToString() ?? "";
        if (action == "open_url" && !string.IsNullOrEmpty(url))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
                return "Only absolute http or https URLs are allowed.";
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
                return $"Opened {uri.AbsoluteUri}";
            }
            catch (Exception ex) { return $"Failed: {ex.Message}"; }
        }
        if (action == "search" && !string.IsNullOrEmpty(query))
        {
            var searchUrl = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}";
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(searchUrl) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
                return $"Searched for \"{query}\"";
            }
            catch (Exception ex) { return $"Failed: {ex.Message}"; }
        }
        return "Browser control: action not recognized or missing parameters.";
    }

    private string HandleDesktopControl(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
        var x = TryIntArg(args, "x");
        var y = TryIntArg(args, "y");
        var amount = TryIntArg(args, "amount");
        try
        {
            switch (action.ToLowerInvariant())
            {
                case "screenshot":
                    RunCommand("snippingtool", "/clip");
                    return "Opened the snippet tool (screen capture).";

                case "move_mouse":
                case "mouse_move":
                    MoveMouse(x, y);
                    return $"Moved mouse to ({x ?? CurrentX()}, {y ?? CurrentY()}).";

                case "click":
                    ClickAt(x, y);
                    return $"Clicked at ({x ?? CurrentX()}, {y ?? CurrentY()}).";

                case "double_click":
                    ClickAt(x, y, doubleClick: true);
                    return $"Double-clicked at ({x ?? CurrentX()}, {y ?? CurrentY()}).";

                case "right_click":
                    RightClickAt(x, y);
                    return $"Right-clicked at ({x ?? CurrentX()}, {y ?? CurrentY()}).";

                case "scroll":
                    Scroll(amount ?? 1);
                    return $"Scrolled {(amount ?? 1)} notch(es).";

                case "focus_window":
                    MoveMouse(x, y);
                    return "Brought the active window to the foreground.";
            }
        }
        catch (Exception e)
        {
            return $"Desktop action '{action}' failed: {e.Message}";
        }
        return $"Desktop action '{action}' not yet implemented.";
    }

    private string HandleWindowManage(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString()?.ToLowerInvariant() ?? "";
        var app = args.GetValueOrDefault("app")?.ToString() ?? "";
        var title = args.GetValueOrDefault("title")?.ToString() ?? "";
        var that = this;
        try
        {
            switch (action)
            {
                case "focus":
                case "focus_app":
                {
                    IntPtr h;
                    if (!string.IsNullOrWhiteSpace(app)) h = Native.FindMainWindow(app);
                    else if (!string.IsNullOrWhiteSpace(title)) h = Native.FindWindowByTitle(title);
                    else h = IntPtr.Zero;
                    if (h == IntPtr.Zero)
                        return $"No window found for '{app}{(!string.IsNullOrEmpty(title) ? title : "")}'.";
                    Native.FocusWindow(h);
                    return $"Focused \"{Native.WindowTitle(h)}\".";
                }

                case "minimize":
                {
                    var fg = Native.GetForegroundWindow();
                    if (fg != IntPtr.Zero && fg != _selfHwnd) { Native.ShowWindow(fg, Native.SW_MINIMIZE); return "Minimized the active window."; }
                    return "Nothing to minimize.";
                }
                case "maximize":
                {
                    var fg = Native.GetForegroundWindow();
                    if (fg != IntPtr.Zero && fg != _selfHwnd) { Native.ShowWindow(fg, Native.SW_MAXIMIZE); return "Maximized the active window."; }
                    return "Nothing to maximize.";
                }
                case "restore":
                {
                    var fg = Native.GetForegroundWindow();
                    if (fg != IntPtr.Zero && fg != _selfHwnd) { Native.ShowWindow(fg, Native.SW_RESTORE); return "Restored the active window."; }
                    return "Nothing to restore.";
                }
                case "close":
                case "close_window":
                    SendInputBatch(new List<NativeInput>
                    {
                        KeyCodeInput(0x12, false, unicode: false),  // Alt
                        KeyCodeInput(0x73, false, unicode: false),  // F4
                        KeyCodeInput(0x73, true, unicode: false),
                        KeyCodeInput(0x12, true, unicode: false),
                    });
                    return "Sent Alt+F4 — closing the active window.";
                case "next_window":
                    SendInputBatch(new List<NativeInput>
                    {
                        KeyCodeInput(0x12, false, unicode: false),  // Alt
                        KeyCodeInput(0x09, false, unicode: false),  // Tab
                        KeyCodeInput(0x09, true, unicode: false),
                        KeyCodeInput(0x12, true, unicode: false),
                    });
                    return "Switched to the next window.";
                case "show_desktop":
                    SendInputBatch(new List<NativeInput>
                    {
                        KeyCodeInput(0x5B, false, unicode: false),  // Win
                        KeyCodeInput(0x44, false, unicode: false),  // D
                        KeyCodeInput(0x44, true, unicode: false),
                        KeyCodeInput(0x5B, true, unicode: false),
                    });
                    return "Showed the desktop.";
                case "list":
                case "list_windows":
                    return Native.ListWindows();
            }
            return $"Window action '{action}' not supported. Try: focus_app, minimize, maximize, restore, close, next_window, show_desktop, list_windows.";
        }
        catch (Exception e)
        {
            return $"Window action '{action}' failed: {e.Message}";
        }
    }

    private static int? TryIntArg(Dictionary<string, object> args, string key)
    {
        var raw = args.GetValueOrDefault(key)?.ToString();
        return int.TryParse(raw, out var v) ? v : (int?)null;
    }

    // ── mouse helpers (SendInput, absolute coords on the primary screen) ──
    private void MoveMouse(int? x, int? y)
    {
        NativeInput? move = null;
        if (x.HasValue && y.HasValue)
        {
            var w = Native.GetSystemMetricsSafe(0);      // SM_CXSCREEN
            var h = Native.GetSystemMetricsSafe(1);      // SM_CYSCREEN
            if (w > 0 && h > 0)
            {
                var ax = (x.Value * 65535) / Math.Max(1, w - 1);
                var ay = (y.Value * 65535) / Math.Max(1, h - 1);
                move = MouseInput(ax, ay, 0, MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE);
            }
        }
        if (!move.HasValue)
        {
            move = MouseInput(x ?? 0, y ?? 0, 0, MOUSEEVENTF_MOVE);
        }
        SendInputBatch(new List<NativeInput> { move.Value });
    }

    private static int CurrentX()
    {
        return Native.GetCursorPos(out var p) ? p.X : 0;
    }

    private static int CurrentY()
    {
        return Native.GetCursorPos(out var p) ? p.Y : 0;
    }

    private void ClickAt(int? x, int? y, bool doubleClick = false, bool right = false)
    {
        if (x.HasValue && y.HasValue) MoveMouse(x, y);
        var down = right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
        var up = right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;
        var repeat = doubleClick ? 2 : 1;
        for (int i = 0; i < repeat; i++)
        {
            SendInputBatch(new List<NativeInput>
            {
                MouseInput(0, 0, 0, down),
                MouseInput(0, 0, 0, up),
            });
            if (i < repeat - 1) System.Threading.Thread.Sleep(40);
        }
    }

    private void RightClickAt(int? x, int? y) => ClickAt(x, y, right: true);

    private void Scroll(int notches)
    {
        notches = Math.Clamp(notches, -100, 100);
        var data = (uint)((int)(notches * 120)); // WHEEL_DELTA
        SendInputBatch(new List<NativeInput>
        {
            MouseInput(0, 0, data, MOUSEEVENTF_WHEEL),
        });
    }

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private static NativeInput MouseInput(int dx, int dy, uint data, uint flags)
    {
        return new NativeInput
        {
            Type = 0, // INPUT_MOUSE
            mi = new NativeMouseInput
            {
                dx = dx,
                dy = dy,
                mouseData = data,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = UIntPtr.Zero,
            },
        };
    }

    private string HandleCodeHelper(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
        var code = args.GetValueOrDefault("code")?.ToString() ?? "";
        var instruction = args.GetValueOrDefault("instruction")?.ToString() ?? "";
        if (string.IsNullOrEmpty(code)) return "No code supplied.";
        // This tool is a placeholder - code analysis is done by the AI model directly.
        return $"Code helper ({action}): This tool is not yet implemented. The AI can analyze code directly in conversation.";
    }

    private string HandleSendMessage(Dictionary<string, object> args)
    {
        var receiver = args.GetValueOrDefault("receiver")?.ToString() ?? "";
        var text = args.GetValueOrDefault("message_text")?.ToString() ?? "";
        var platform = args.GetValueOrDefault("platform")?.ToString() ?? "sms";
        // Actual sending requires ADB/phone integration; this tool logs the intent.
        var logEntry = $"[Message to {receiver} via {platform}] {text}";
        _ = Task.Run(async () =>
        {
            try { await _memory.LogAsync("system", logEntry); }
            catch { }
        });
        return $"Message logged for {receiver} via {platform}: \"{text}\". (Sending requires phone_* ADB tools; not yet implemented via this tool.)";
    }

    private string HandleSetAway(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString()?.ToLowerInvariant() ?? "on";
        var on = action is not ("off" or "false" or "0" or "disable");
        _settings.AwayMode = on;
        _settings.Save();
        _ = _gemini.SetAwayAsync(on);
        Dbg($"Away mode set to {on}.");
        return on
            ? "Away mode is ON. I will watch your system and message you with anything important."
            : "Away mode is OFF. I am back to speaking alerts here.";
    }

    private void OnContactRequested(ContactRequest req)
    {
        Dbg($"Contact requested [{req.Area}/{req.Priority}]: {AppLog.Redact(req.Text)}");
        _ = RouteContactAsync(req);
    }

    private async Task RouteContactAsync(ContactRequest req)
    {
        try
        {
            var result = await _outreach.SendAsync(req.Text, req.Area, req.Priority);
            LogEnqueued("system", $"Contact [{req.Area}]: {result}");
        }
        catch (Exception ex)
        {
            Dbg($"Contact routing failed: {ex.Message}");
        }
    }

    private string HandleYouTubeVideo(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
        var query = args.GetValueOrDefault("query")?.ToString() ?? "";
        if (action == "search" && !string.IsNullOrEmpty(query))
        {
            var url = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}";
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
                return $"Searching YouTube for \"{query}\"";
            }
            catch (Exception ex) { return $"Failed: {ex.Message}"; }
        }
        return "YouTube action not recognized.";
    }

    /* ===================== SENDINPUT NATIVE HELPERS ===================== */

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeKeyboardInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    // Exact native sizeof(INPUT): DWORD type @0, 4 bytes pad, then the union.
    // Declaring both keyboard+mouse members at offset 8 makes the union 32 bytes
    // on x64, so Marshal.SizeOf == 40 (the real size Windows expects). Passing a
    // wrong cbSize makes SendInput reject the batch AND read past the buffer.
    [StructLayout(LayoutKind.Explicit)]
    internal struct NativeInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public NativeKeyboardInput kb;
        [FieldOffset(8)] public NativeMouseInput mi;
        public static int Size => Marshal.SizeOf(typeof(NativeInput));
    }

    internal static class Native
    {
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, NativeInput[] pInputs, int cbSize);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int count);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool GetCursorPos(out NativePoint point);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern int GetSystemMetrics(int index);

        public static int GetSystemMetricsSafe(int index)
        {
            try { return GetSystemMetrics(index); }
            catch { return 0; }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct NativePoint { public int X; public int Y; }

        public const int SW_RESTORE = 9;
        public const int SW_MINIMIZE = 6;
        public const int SW_MAXIMIZE = 3;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct NativeRect { public int Left, Top, Right, Bottom; }

        public static NativeRect GetWindowRectR(IntPtr hWnd)
        {
            if (!GetWindowRect(hWnd, out var r)) return new NativeRect();
            return r;
        }

        public static string WindowTitle(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return "";
            var sb = new System.Text.StringBuilder(256);
            GetWindowTextW(hWnd, sb, 256);
            return sb.ToString();
        }

        public static void FocusWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return;
            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
            SetForegroundWindow(hWnd);
        }

        // Find the main (largest, titled, visible, non-minimized) top-level window
        // owned by a process whose image name matches (e.g. "notepad").
        public static IntPtr FindMainWindow(string imageNameWithoutExt)
        {
            if (string.IsNullOrWhiteSpace(imageNameWithoutExt)) return IntPtr.Zero;
            var target = imageNameWithoutExt.Trim().ToLowerInvariant();
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h)) return true;
                if (GetWindowThreadProcessId(h, out uint pid) == 0) return true;
                if (pid == 0) return true;
                string pname;
                try { pname = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
                catch { return true; }
                if (pname != target) return true;
                if (IsIconic(h)) return true;
                var title = WindowTitle(h);
                if (string.IsNullOrWhiteSpace(title)) return true;
                var r = GetWindowRectR(h);
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = h; }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        // Find a visible top-level window whose title contains the given fragment.
        public static IntPtr FindWindowByTitle(string titleFragment)
        {
            if (string.IsNullOrWhiteSpace(titleFragment)) return IntPtr.Zero;
            var frag = titleFragment.Trim().ToLowerInvariant();
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h)) return true;
                if (IsIconic(h)) return true;
                var t = WindowTitle(h);
                if (t.IndexOf(frag, StringComparison.OrdinalIgnoreCase) < 0) return true;
                var r = GetWindowRectR(h);
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = h; }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        // List visible titled windows as "title (processName)".
        public static string ListWindows()
        {
            var names = new List<string>();
            EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h)) return true;
                var title = WindowTitle(h);
                if (string.IsNullOrWhiteSpace(title)) return true;
                string pname = "";
                if (GetWindowThreadProcessId(h, out uint pid) != 0 && pid != 0)
                {
                    try { pname = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
                    catch { }
                }
                names.Add($"\"{title}\" ({pname})");
                return true;
            }, IntPtr.Zero);
            if (names.Count == 0) return "No visible windows.";
            return string.Join(", ", names.Take(14));
        }
    }
}
