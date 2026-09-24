using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using QRCoder;
using Ultron.Models;

namespace Ultron.Services;

/// <summary>
/// Handles the Settings dialog UI and all settings-related UI logic.
/// Extracted from MainWindow to reduce its size and separate concerns.
/// </summary>
public sealed class SettingsDialogService
{
    private readonly MainWindow _mainWindow;
    private readonly AppSettings _settings;
    private readonly GeminiBackend _gemini;

    public SettingsDialogService(MainWindow mainWindow, AppSettings settings, GeminiBackend gemini)
    {
        _mainWindow = mainWindow;
        _settings = settings;
        _gemini = gemini;
    }

    public async Task OpenSettingsAsync()
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
                    await _mainWindow._memory.PurgeAllAsync();
                    _mainWindow.DispatcherQueue.TryEnqueue(() => _mainWindow.AddMessage("system", "Local memory (conversations + facts) purged."));
                }
                catch (Exception ex) { MainWindow.Dbg($"purge failed: {ex.Message}"); }
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
            var token = _mainWindow._dashboard?.Token ?? DashboardServer.NewToken();
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
        var tgFallbackEnabled = new ToggleSwitch { Header = "Enable call fallback messaging", IsOn = _settings.CallFallbackEnabled };
        var tgFallbackThreshold = new TextBox { Text = _settings.CallFallbackThresholdSeconds.ToString(), Width = 80, PlaceholderText = "5" };
        var tgFallbackChatId = new TextBox { Text = _settings.CallFallbackChatId, Width = 200, PlaceholderText = "@username or chat ID" };

        _mainWindow._tgStatusText = new TextBlock
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
        var tgHint = new TextBlock
        {
            FontFamily = font,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Foreground = tgMuted,
            Text = "How to get credentials: sign in on my.telegram.org with the ULTRON account's phone " +
                   "number, open 'API development tools', and copy the api_id and api_hash below. " +
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
                _mainWindow._tgStatusText.Text = "Enter api_id and api_hash before connecting.";
                _mainWindow._tgStatusText.Foreground = tgAccent;
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
            _mainWindow._gemini.TelegramOptions = new TelegramCallOptions(id, hash, phone, tgToggle.IsOn, tgTarget.Text.Trim());
            _mainWindow._tgStatusText.Text = "Connecting to Telegram…";
            _mainWindow._tgStatusText.Foreground = tgMuted;
            await _mainWindow._gemini.SendTelegramLoginAsync(phone, id, hash);
        };
        tgResolve.Click += async (_, _) =>
        {
            var target = tgTarget.Text.Trim();
            if (target.Length == 0)
            {
                _mainWindow._tgStatusText.Text = "Enter a call target first.";
                _mainWindow._tgStatusText.Foreground = tgAccent;
                return;
            }
            lock (_settings)
            {
                _settings.TelegramCallTarget = target;
                _settings.Save();
            }
            _mainWindow._tgStatusText.Text = $"Resolving {target}…";
            _mainWindow._tgStatusText.Foreground = tgMuted;
            await _mainWindow._gemini.SendTelegramResolveTargetAsync(target);
        };
        tgLogout.Click += async (_, _) =>
        {
            _mainWindow._tgStatusText.Text = "Logging out of Telegram…";
            _mainWindow._tgStatusText.Foreground = tgMuted;
            await _mainWindow._gemini.SendTelegramLogoutAsync();
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
            if (_mainWindow._tgStatusText is not null) _mainWindow._tgStatusText.Text = "Submitting code…";
            await _mainWindow._gemini.SendTelegramCodeAsync(code);
            tgCodeSubmit.IsEnabled = true;
            tgCodeBox.Text = "";
        };
        tgCodePanel.Children.Add(Lbl("Enter the login code Telegram sent (check the ULTRON account):"));
        tgCodePanel.Children.Add(tgCodeBox);
        tgCodePanel.Children.Add(tgCodeSubmit);
        _mainWindow._tgCodePanel = tgCodePanel;
        _mainWindow._tgCodeBox = tgCodeBox;

        var dialog = new ContentDialog
        {
            XamlRoot = _mainWindow.Content.XamlRoot,
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
                        _mainWindow._tgStatusText,
                        Lbl("Call target (the Telegram account that should be called):"),
                        tgTarget,
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { tgResolve } },

                        // Test Call button
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = {
                            Lbl("Test Call:", 11),
                            new Button { Content = "📞 Test Call", Padding = new Thickness(12, 6, 12, 6),
                                Background = new SolidColorBrush(Microsoft.UI.Colors.DarkGreen), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                                CornerRadius = new CornerRadius(6), FontFamily = font, FontSize = 11 }
                        }},

                        // Call fallback settings
                        Lbl("CALL FALLBACK — send Telegram message if call unanswered or hung up immediately:"),
                        tgFallbackEnabled,
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
                        _mainWindow.EnsureDashboard(webPort);
                        _mainWindow.AddMessage("system", _mainWindow._dashboardUrl is not null
                            ? $"Web deck live — scan the QR or open {_mainWindow._dashboardUrl}"
                            : "Web deck failed to start. See ultron.log.");
                    }
                    else
                    {
                        _mainWindow._dashboard?.Dispose();
                        _mainWindow._dashboard = null;
                        _mainWindow._dashboardUrl = null;
                        _mainWindow.AddMessage("system", "Web deck disabled.");
                    }
                }

                _settings.Save();
                _mainWindow.ApplyHotkeyBindings();

                foreach (var n in notifications) _mainWindow.AddMessage("system", n);
            }
            if (!string.IsNullOrEmpty(key))
                _mainWindow.AddMessage("system", $"CORE ONLINE — Gemini brain configured (voice: {voiceOptions.SelectedItem?.ToString() ?? "Charon"})");
            else
                _mainWindow.AddMessage("system", "Settings saved.");
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
                                btn.Click += async (_, _) => await _mainWindow._gemini.SendTelegramCallStartAsync();
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
            _mainWindow._tgStatusText = null;
            _mainWindow._tgCodePanel = null;
            _mainWindow._tgCodeBox = null;
        }
    }

    /// <summary>Collapse + release the inline code entry controls (called after a
    /// successful login, or when settings closes).</summary>
    public void HideTelegramCodePanel()
    {
        if (_mainWindow._tgCodePanel is not null) _mainWindow._tgCodePanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Ask the user for the Telegram login code (or 2FA password) that
    /// arrived on the ULTRON account, then forward it to the backend. When the
    /// settings dialog is open the entry appears inline in it; otherwise a
    /// standalone dialog is used.</summary>
    public void PromptTelegramCode(string phone, string hint)
    {
        try
        {
            if (_mainWindow._tgCodePanel is not null)
            {
                _mainWindow._tgCodeBox!.Text = "";
                _mainWindow._tgCodePanel.Visibility = Visibility.Visible;
                _mainWindow._tgCodeBox.Focus(FocusState.Programmatic);
                if (_mainWindow._tgStatusText is not null) _mainWindow._tgStatusText.Text = $"Enter the Telegram code sent to {phone}.";
                return;
            }
            var codeBox = new TextBox { PlaceholderText = "Login code", Width = 300 };
            var dlg = new ContentDialog
            {
                XamlRoot = _mainWindow.Content?.XamlRoot,
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
                _mainWindow.AddMessage("system", $"Telegram needs a login code: {hint}");
                return;
            }
            _ = dlg.ShowAsync().AsTask().ContinueWith(async t =>
            {
                if (t.IsCompletedSuccessfully && t.Result == ContentDialogResult.Primary && codeBox.Text.Trim().Length > 0)
                    await _mainWindow._gemini.SendTelegramCodeAsync(codeBox.Text.Trim());
            }, System.Threading.Tasks.TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            MainWindow.Dbg($"code prompt failed: {ex.Message}");
            _mainWindow.AddMessage("system", $"Telegram needs a login code: {hint}");
        }
    }

    private static string DeckUrl(int port, string token) =>
        $"http://{Ultron.Services.DashboardServer.LanIp()}:{port}/?k={token}";

    private static WriteableBitmap BuildDeckQr(int port, string token, int px = 240)
    {
        var qr = new QRCoder.QRCodeGenerator();
        var data = qr.CreateQrCode($"http://{Ultron.Services.DashboardServer.LanIp()}:{port}/?k={token}", QRCoder.QRCodeGenerator.ECCLevel.M, forceUtf8: true);
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
                    buf[i] = 0x1A; buf[i + 1] = 0x1A; buf[i + 2] = 0x1A; buf[i + 3] = 0xFF;
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
}