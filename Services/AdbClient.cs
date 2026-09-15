using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ultron.Services;

/// <summary>
/// Full-featured ADB client ported from the legacy Python implementation.
/// Provides ~100 operations across connection, power, screen, input, navigation,
/// camera, clipboard, files, settings, network, calls/SMS, contacts, media, and system.
/// </summary>
public sealed class AdbClient
{
    private readonly string _adbPath;
    private readonly string _host;
    private readonly int _port;
    private readonly string _serial;
    private string _cameraFacing = "back";

    public AdbClient(string adbPath, string host, int port = 5555, string? serial = null)
    {
        _adbPath = adbPath;
        _host = host;
        _port = port;
        _serial = serial ?? "";
    }

    // ════════════════════════════════════════════════════════════════
    //  CORE EXECUTION
    // ════════════════════════════════════════════════════════════════

    private string Run(string[] args, int timeout = 15)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _adbPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null) return "";
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeout * 1000))
            {
                try { proc.Kill(); } catch { }
                return "";
            }
            var o = stdout.GetAwaiter().GetResult().Trim();
            var e = stderr.GetAwaiter().GetResult().Trim();
            return string.IsNullOrEmpty(o) ? e : o;
        }
        catch
        {
            return "";
        }
    }

    private static readonly string[] ShellArg = ["shell"];
    private static readonly string[] DevicesArg = ["devices"];

    private string Shell(string cmd, int timeout = 15)
    {
        return Run(ShellArg.Concat(cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray(), timeout);
    }

    private string ShellRaw(string cmd, int timeout = 15)
    {
        return Run(new[] { "shell", cmd }, timeout);
    }

    // ════════════════════════════════════════════════════════════════
    //  CONNECTION
    // ════════════════════════════════════════════════════════════════

    public bool IsConnected()
    {
        var out1 = Run(DevicesArg);
        foreach (var line in out1.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            if (t.StartsWith("List of", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal)) continue;
            if (t.Contains("device") && !t.Contains("offline")) return true;
        }
        return false;
    }

    private bool ConnectRaw(int timeout = 8)
    {
        var result = Run(new[] { "connect", $"{_host}:{_port}" }, timeout);
        return result.Contains("connected", StringComparison.OrdinalIgnoreCase)
            || result.Contains("already connected", StringComparison.OrdinalIgnoreCase);
    }

    private bool VerifySerial()
    {
        if (string.IsNullOrEmpty(_serial)) return true;
        var serial = Shell("getprop ro.serialno").Trim();
        return !string.IsNullOrEmpty(serial) && serial == _serial;
    }

    private void DisconnectRaw()
    {
        try { Run(new[] { "disconnect", $"{_host}:{_port}" }, 8); } catch { }
    }

    public bool Connect()
    {
        if (IsConnected()) return true;
        var addr = _host;
        if (string.IsNullOrEmpty(addr)) return false;
        if (ConnectRaw())
        {
            if (VerifySerial()) return true;
            DisconnectRaw();
        }
        return false;
    }

    public string Connected()
    {
        return Connect() ? "Phone is connected." : "Phone NOT connected — set PHONE_ADDR (VPN address) and pair this PC with the phone.";
    }

    public void Disconnect() => DisconnectRaw();

    // ════════════════════════════════════════════════════════════════
    //  POWER
    // ════════════════════════════════════════════════════════════════

    public string DeviceInfo()
    {
        if (!Connect()) return "Phone not connected.";
        var model = Shell("getprop ro.product.model");
        var android = Shell("getprop ro.build.version.release");
        var brand = Shell("getprop ro.product.brand");
        var res = Shell("wm size");
        var density = Shell("wm density");
        var bat = ShellRaw("dumpsys battery");
        var (level, charging, temp) = ("", "", "");
        foreach (var line in bat.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("level:")) level = line.Split(':').Last().Trim();
            if (line.Contains("temperature:")) temp = line.Split(':').Last().Trim();
            if (line.Contains("AC powered:") && line.Contains("true")) charging = "AC";
            if (line.Contains("USB powered:") && line.Contains("true")) charging = "USB";
            if (line.Contains("Wireless powered:") && line.Contains("true")) charging = "wireless";
        }
        var tempC = int.TryParse(temp, out var t) ? $"{t / 10}C" : temp;
        var chargingStr = charging.Length > 0 ? $" ({charging})" : "";
        return $"{brand} {model} | Android {android} | {res} {density} | Battery: {level}%{chargingStr} {tempC}";
    }

    public string Battery()
    {
        var bat = ShellRaw("dumpsys battery");
        var info = new Dictionary<string, string>();
        foreach (var line in bat.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains(':'))
            {
                var parts = line.Split(':', 2);
                info[parts[0].Trim()] = parts[1].Trim();
            }
        }
        var level = info.GetValueOrDefault("level", "?");
        var status = info.GetValueOrDefault("status", "?");
        var temp = info.GetValueOrDefault("temperature", "?");
        var voltage = info.GetValueOrDefault("voltage", "?");
        var health = info.GetValueOrDefault("health", "?");
        var statusMap = new Dictionary<string, string> { { "1", "unknown" }, { "2", "charging" }, { "3", "discharging" }, { "4", "not charging" }, { "5", "full" } };
        var healthMap = new Dictionary<string, string> { { "1", "unknown" }, { "2", "good" }, { "3", "overheat" }, { "4", "dead" }, { "5", "over voltage" }, { "6", "unspecified failure" } };
        var tempStr = int.TryParse(temp, out var t) ? $"{t / 10}C" : temp;
        return $"Battery: {level}% | {statusMap.GetValueOrDefault(status, status)} | {tempStr} | {voltage}mV | {healthMap.GetValueOrDefault(health, health)}";
    }

    public string Reboot() { Shell("reboot"); return "Rebooting phone..."; }
    public string RebootRecovery() { Shell("reboot recovery"); return "Rebooting to recovery mode..."; }
    public string RebootBootloader() { Shell("reboot bootloader"); return "Rebooting to bootloader..."; }
    public string Shutdown() { Shell("reboot -p"); return "Shutting down phone..."; }
    public string PowerOff() => Shutdown();
    public string SafeMode() { Shell("setprop persist.sys.safemode 1"); Shell("reboot"); return "Rebooting into safe mode..."; }

    // ════════════════════════════════════════════════════════════════
    //  SCREEN
    // ════════════════════════════════════════════════════════════════

    public string ScreenOn() { Shell("input keyevent KEYCODE_WAKEUP"); Thread.Sleep(300); Shell("input keyevent KEYCODE_MENU"); return "Screen on."; }
    public string ScreenOff() { Shell("input keyevent KEYCODE_SLEEP"); return "Screen off."; }

    public bool IsScreenOn() =>
        ShellRaw("dumpsys power | grep mWakefulness").Contains("Awake", StringComparison.OrdinalIgnoreCase);

    public bool IsLocked()
    {
        var out1 = ShellRaw("dumpsys window | grep -E 'mDreamingLockscreen|mShowingLockscreen|isStatusBarKeyguard'");
        if (out1.Contains("mDreamingLockscreen=true") || out1.Contains("mShowingLockscreen=true")) return true;
        var out2 = ShellRaw("dumpsys window | grep 'isStatusBarKeyguard'");
        return out2.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsInUse()
    {
        if (!IsScreenOn()) return false;
        var activity = ShellRaw("dumpsys activity activities | grep -E 'ResumedActivity:'");
        var m = Regex.Match(activity, @"u0 (\S+?)/");
        var pkg = m.Success ? m.Groups[1].Value : "";
        if (string.IsNullOrEmpty(pkg) || pkg.Contains("launcher", StringComparison.OrdinalIgnoreCase)) return false;
        var ime = ShellRaw("dumpsys input_method | grep mInputShown");
        if (ime.Contains("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(pkg) && !pkg.Contains("launcher", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public string Unlock()
    {
        Shell("input keyevent KEYCODE_WAKEUP");
        Thread.Sleep(500);
        Shell("input swipe 540 2000 540 500 300");
        Thread.Sleep(500);
        var lockCheck = ShellRaw("dumpsys window | grep mDreamingLockscreen");
        if (lockCheck.Contains("true", StringComparison.OrdinalIgnoreCase))
            return "Screen woke but lock screen still showing (PIN/pattern required).";
        return "Screen unlocked.";
    }

    public string UnlockWithPin(string pin)
    {
        Shell("input keyevent KEYCODE_WAKEUP");
        Thread.Sleep(500);
        Shell("input swipe 540 2000 540 500 300");
        Thread.Sleep(1000);
        ShellRaw($"input text {pin}");
        Thread.Sleep(300);
        Shell("input keyevent KEYCODE_ENTER");
        Thread.Sleep(1000);
        var lockCheck = ShellRaw("dumpsys window | grep mDreamingLockscreen");
        if (lockCheck.Contains("true", StringComparison.OrdinalIgnoreCase))
            return "PIN entered but lock screen still showing — PIN may be incorrect.";
        return "Phone unlocked with PIN.";
    }

    public string ScreenBrightness(int level)
    {
        level = Math.Clamp(level, 0, 255);
        ShellRaw($"settings put system screen_brightness {level}");
        return $"Brightness set to {level}.";
    }

    public string ScreenRotation(bool auto)
    {
        ShellRaw($"settings put system accelerometer_rotation {(auto ? 1 : 0)}");
        return auto ? "Auto-rotation on." : "Auto-rotation off.";
    }

    public string ScreenRotate(int degrees)
    {
        ShellRaw($"settings put system user_rotation {degrees / 90}");
        return $"Screen rotated to {degrees} degrees.";
    }

    public string Screenshot()
    {
        const string remote = "/sdcard/ultron_screenshot.png";
        var local = Path.Combine(Path.GetTempPath(), "ultron_screenshot.png");
        Shell($"screencap -p {remote}");
        Run(new[] { "pull", remote, local });
        Shell($"rm {remote}");
        return File.Exists(local) ? local : "Screenshot failed.";
    }

    public string ScreenRecord(int seconds = 10)
    {
        seconds = Math.Min(seconds, 180);
        const string remote = "/sdcard/ultron_recording.mp4";
        var local = Path.Combine(Path.GetTempPath(), "ultron_recording.mp4");
        ShellRaw($"screenrecord --time-limit {seconds} {remote}");
        Run(new[] { "pull", remote, local });
        Shell($"rm {remote}");
        return File.Exists(local) ? local : "Recording failed.";
    }

    // ════════════════════════════════════════════════════════════════
    //  INPUT
    // ════════════════════════════════════════════════════════════════

    public string Home() { Shell("input keyevent KEYCODE_HOME"); return "Home pressed."; }
    public string Back() { Shell("input keyevent KEYCODE_BACK"); return "Back pressed."; }
    public string Recent() { Shell("input keyevent KEYCODE_APP_SWITCH"); return "Recent apps."; }
    public string Tap(int x, int y) { Shell($"input tap {x} {y}"); return $"Tapped ({x}, {y})."; }
    public string LongPress(int x, int y, int durationMs = 1000) { Shell($"input swipe {x} {y} {x} {y} {durationMs}"); return $"Long pressed ({x}, {y})."; }
    public string Swipe(int x1, int y1, int x2, int y2, int durationMs = 300) { Shell($"input swipe {x1} {y1} {x2} {y2} {durationMs}"); return $"Swiped ({x1},{y1}) to ({x2},{y2})."; }
    public string SwipeUp() { Shell("input swipe 540 1800 540 500 300"); return "Swiped up."; }
    public string SwipeDown() { Shell("input swipe 540 500 540 1800 300"); return "Swiped down."; }
    public string SwipeLeft() { Shell("input swipe 900 1200 100 1200 300"); return "Swiped left."; }
    public string SwipeRight() { Shell("input swipe 100 1200 900 1200 300"); return "Swiped right."; }

    public string TypeText(string text)
    {
        var escaped = text.Replace(" ", "%s").Replace("&", "\\&").Replace("'", "\\'").Replace("\"", "\\\"");
        ShellRaw($"input text '{escaped}'");
        return $"Typed: {text}";
    }

    public string TypeTextSlow(string text)
    {
        foreach (var ch in text)
        {
            if (ch == ' ') Shell("input keyevent KEYCODE_SPACE");
            else ShellRaw($"input text '{ch}'");
            Thread.Sleep(50);
        }
        return $"Typed slowly: {text}";
    }

    public string KeyEvent(string keycode) { Shell($"input keyevent {keycode}"); return $"Key event {keycode} sent."; }
    public string VolumeUp() { Shell("input keyevent KEYCODE_VOLUME_UP"); return "Volume up."; }
    public string VolumeDown() { Shell("input keyevent KEYCODE_VOLUME_DOWN"); return "Volume down."; }
    public string VolumeMute() { Shell("input keyevent KEYCODE_VOLUME_MUTE"); return "Muted."; }

    public string SetVolume(int level)
    {
        ShellRaw($"media volume --set {level} --stream 3");
        return $"Media volume set to {level}.";
    }

    public string GetVolume()
    {
        var out1 = ShellRaw("media volume --get --stream 3");
        return !string.IsNullOrEmpty(out1) ? out1 : "Could not get volume.";
    }

    // ════════════════════════════════════════════════════════════════
    //  NAVIGATION / APPS
    // ════════════════════════════════════════════════════════════════

    public string OpenApp(string package)
    {
        if (package is "camera" or "cam") return OpenCamera();
        if (package is "settings" or "setting") return ShellRaw("am start -a android.settings.SETTINGS") ?? "Settings opened.";
        if (package is "chrome" or "browser") return ShellRaw("am start -a android.intent.action.VIEW -d http://www.google.com") ?? "Browser opened.";
        if (package is "phone" or "dialer" or "dial") return ShellRaw("am start -a android.intent.action.DIAL") ?? "Dialer opened.";
        if (package is "contacts" or "contact") return ShellRaw("am start -a android.intent.action.VIEW -d content://com.android.contacts/contacts") ?? "Contacts opened.";
        if (package is "messages" or "sms" or "msg") return ShellRaw("am start -a android.intent.action.MAIN -c android.intent.category.APP_MESSAGING") ?? "Messages opened.";
        if (package is "gallery" or "photos") return ShellRaw("am start -a android.intent.action.VIEW -d content://media/external/images/media") ?? "Gallery opened.";
        if (package == "youtube")
        {
            var comp = ResolveLaunchIntent("com.google.android.youtube");
            if (comp is not null) { ShellRaw($"am start -n {comp}"); return "YouTube opened."; }
        }
        var resolved = ResolveLaunchIntent(package);
        if (resolved is not null) { ShellRaw($"am start -n {resolved}"); return $"Opened {package}."; }
        Shell($"monkey -p {package} -c android.intent.category.LAUNCHER 1");
        return $"Opened {package} (via monkey fallback).";
    }

    public string CloseApp(string package) { ShellRaw($"am force-stop {package}"); return $"Closed {package}."; }
    public string ClearAppData(string package) { ShellRaw($"pm clear {package}"); return $"Cleared data for {package}."; }

    public string ListApps()
    {
        var out1 = Shell("pm list packages -3");
        var pkgs = out1.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("package:", StringComparison.Ordinal)).Select(l => l.Replace("package:", "")).ToList();
        if (pkgs.Count == 0) return "No third-party apps found.";
        return $"Third-party apps ({pkgs.Count}):\n" + string.Join("\n", pkgs.OrderBy(p => p).Select(p => $"  - {p}"));
    }

    public string ListAllApps()
    {
        var out1 = Shell("pm list packages");
        var pkgs = out1.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("package:", StringComparison.Ordinal)).Select(l => l.Replace("package:", "")).ToList();
        return $"All apps ({pkgs.Count}):\n" + string.Join("\n", pkgs.OrderBy(p => p).Select(p => $"  - {p}"));
    }

    public string AppInfo(string package)
    {
        var out1 = ShellRaw($"dumpsys package {package} | head -30");
        return !string.IsNullOrEmpty(out1) ? out1 : $"Could not get info for {package}.";
    }

    public string IsAppRunning(string package)
    {
        var out1 = ShellRaw($"pidof {package}");
        return !string.IsNullOrEmpty(out1) ? $"{package} is running (PID: {out1})." : $"{package} is NOT running.";
    }

    public string CurrentActivity()
    {
        var out1 = ShellRaw("dumpsys activity activities | grep -E 'ResumedActivity:|topResumedActivity'");
        if (string.IsNullOrEmpty(out1))
            out1 = ShellRaw("dumpsys activity activities | grep mResumedActivity");
        return !string.IsNullOrEmpty(out1) ? out1 : "Could not determine current activity.";
    }

    private string? ResolveLaunchIntent(string package)
    {
        var out1 = ShellRaw($"cmd package resolve-activity --brief {package}");
        foreach (var line in out1.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains('/') && !line.StartsWith("priority", StringComparison.Ordinal))
                return line.Trim();
        }
        return null;
    }

    // ════════════════════════════════════════════════════════════════
    //  CAMERA
    // ════════════════════════════════════════════════════════════════

    public string OpenCamera()
    {
        _cameraFacing = "back";
        var comp = ResolveLaunchIntent("com.hihonor.camera");
        if (comp is not null) { ShellRaw($"am start -n {comp}"); return "Camera opened."; }
        var r = ShellRaw("am start -n com.hihonor.camera/.Camera");
        if (r.Contains("Error"))
            r = ShellRaw("am start -a android.media.action.STILL_IMAGE_CAMERA");
        return !r.Contains("Error") ? "Camera opened." : $"Camera failed: {r}";
    }

    public string TakePhoto()
    {
        Thread.Sleep(500);
        Shell("input tap 540 2200");
        Thread.Sleep(1000);
        return "Photo taken.";
    }

    public string TakeSelfie()
    {
        Thread.Sleep(500);
        if (_cameraFacing != "front") { Shell("input tap 950 2100"); Thread.Sleep(1000); }
        Shell("input tap 540 2200");
        Thread.Sleep(1000);
        return "Selfie taken.";
    }

    public string ToggleFlash()
    {
        ShellRaw("am broadcast -a com.hihonor.camera.FLASH_TOGGLE");
        Thread.Sleep(500);
        return "Flash toggled.";
    }

    public string SwitchCamera()
    {
        Shell("input tap 950 2100");
        Thread.Sleep(1000);
        _cameraFacing = _cameraFacing == "back" ? "front" : "back";
        return $"Camera switched to {_cameraFacing}.";
    }

    public string GetCameraFacing() => _cameraFacing;

    public string SelfieVerify()
    {
        if (!Connect()) return "Phone not connected.";
        if (!IsScreenOn()) { ScreenOn(); Thread.Sleep(1000); }
        if (IsLocked())
        {
            var pin = "";
            if (string.IsNullOrEmpty(pin))
                return "Phone is locked and PHONE_PIN is not set — configure it to auto-unlock for selfies.";
            UnlockWithPin(pin);
            Thread.Sleep(2000);
        }
        if (IsInUse()) return "Phone in use, try again later.";
        OpenCamera();
        Thread.Sleep(2000);
        if (_cameraFacing != "front") { SwitchCamera(); Thread.Sleep(1000); }
        TakeSelfie();
        Thread.Sleep(2000);
        var out1 = ShellRaw("ls -t /sdcard/DCIM/Camera/");
        var photos = out1.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(f => f.Trim().EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.Trim().EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || f.Trim().EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
        if (photos.Count == 0) return "No photo found after selfie.";
        var phonePath = $"/sdcard/DCIM/Camera/{photos[0]}";
        var local = PullFile(phonePath);
        if (File.Exists(local) && new FileInfo(local).Length > 1000)
            return "Selfie captured.";
        return "Failed to pull selfie.";
    }

    public string RecordVideo(int seconds = 10)
    {
        Thread.Sleep(500);
        Shell("input tap 540 2200");
        Thread.Sleep(500);
        Shell("input keyevent KEYCODE_CAMERA");
        Thread.Sleep(seconds * 1000);
        Shell("input keyevent KEYCODE_CAMERA");
        return $"Recorded {seconds}s video.";
    }

    // ════════════════════════════════════════════════════════════════
    //  CLIPBOARD
    // ════════════════════════════════════════════════════════════════

    public string GetClipboard()
    {
        var out1 = ShellRaw("dumpsys clipboard");
        foreach (var line in out1.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("primary", StringComparison.OrdinalIgnoreCase) || line.Contains("text", StringComparison.OrdinalIgnoreCase))
                return line.Trim();
        }
        return !string.IsNullOrEmpty(out1) ? out1 : "Clipboard is empty.";
    }

    public string SetClipboard(string text)
    {
        ShellRaw($"am broadcast -a clipper.set -e text \"{text}\"");
        ShellRaw($"input text '{text.Replace(" ", "%s")}'");
        return $"Clipboard set: {text}";
    }

    public string ShareText(string text)
    {
        ShellRaw($"am start -a android.intent.action.SEND -t text/plain --es android.intent.extra.TEXT \"{text}\"");
        return "Share dialog opened.";
    }

    public string ShareFile(string path)
    {
        ShellRaw($"am start -a android.intent.action.SEND -t application/octet-stream --eu android.intent.extra.STREAM \"file://{path}\"");
        return $"Sharing {path}.";
    }

    // ════════════════════════════════════════════════════════════════
    //  FILES
    // ════════════════════════════════════════════════════════════════

    public string ListFiles(string path = "/sdcard/")
    {
        var out1 = ShellRaw($"ls -la {path}");
        return !string.IsNullOrEmpty(out1) ? out1 : $"Could not list {path}";
    }

    public string ListDownloads() => ListFiles("/sdcard/Download/");
    public string ListPictures() => ListFiles("/sdcard/DCIM/Camera/");
    public string ListDocuments() => ListFiles("/sdcard/Documents/");
    public string ListMusic() => ListFiles("/sdcard/Music/");
    public string ListVideos() => ListFiles("/sdcard/DCIM/Camera/") + "\n" + ListFiles("/sdcard/Movies/");

    public string FindFiles(string name)
    {
        var out1 = ShellRaw($"find /sdcard -name '*{name}*' -type f 2>/dev/null | head -20");
        return !string.IsNullOrEmpty(out1) ? out1 : $"No files matching '{name}' found.";
    }

    public string DeleteFile(string path) { ShellRaw($"rm -f {path}"); return $"Deleted {path}."; }
    public string DeleteFolder(string path) { ShellRaw($"rm -rf {path}"); return $"Deleted folder {path}."; }

    public string FileInfo(string path)
    {
        var out1 = ShellRaw($"ls -la {path}");
        return !string.IsNullOrEmpty(out1) ? out1 : $"File not found: {path}";
    }

    public string StorageInfo()
    {
        var out1 = Shell("df -h /sdcard");
        return !string.IsNullOrEmpty(out1) ? out1 : "Could not get storage info.";
    }

    public string PullFile(string phonePath, string? localDir = null)
    {
        localDir ??= Path.GetTempPath();
        var local = Path.Combine(localDir, Path.GetFileName(phonePath));
        Run(new[] { "pull", phonePath, local });
        return File.Exists(local) ? local : $"Failed to pull {phonePath}.";
    }

    public string PushFile(string localPath, string phonePath = "/sdcard/Download/")
    {
        Run(new[] { "push", localPath, phonePath });
        return $"Pushed to {phonePath}.";
    }

    public string CreateFolder(string path) { ShellRaw($"mkdir -p {path}"); return $"Created {path}."; }
    public string MoveFile(string src, string dst) { ShellRaw($"mv {src} {dst}"); return $"Moved {src} to {dst}."; }
    public string CopyFile(string src, string dst) { ShellRaw($"cp {src} {dst}"); return $"Copied {src} to {dst}."; }

    public string SearchFiles(string query)
    {
        var out1 = ShellRaw($"find /sdcard -iname '*{query}*' 2>/dev/null | head -20");
        return !string.IsNullOrEmpty(out1) ? out1 : $"No files matching '{query}'.";
    }

    // ════════════════════════════════════════════════════════════════
    //  SETTINGS
    // ════════════════════════════════════════════════════════════════

    public string WifiOn() { ShellRaw("svc wifi enable"); return "WiFi turned on."; }
    public string WifiOff() { ShellRaw("svc wifi disable"); return "WiFi turned off."; }
    public string WifiStatus()
    {
        var out1 = ShellRaw("dumpsys wifi | grep 'Wi-Fi is'");
        if (string.IsNullOrEmpty(out1)) out1 = ShellRaw("settings get global wifi_on");
        return !string.IsNullOrEmpty(out1) ? out1 : "Could not get WiFi status.";
    }

    public string BluetoothOn() { ShellRaw("svc bluetooth enable"); return "Bluetooth turned on."; }
    public string BluetoothOff() { ShellRaw("svc bluetooth disable"); return "Bluetooth turned off."; }
    public string BluetoothStatus()
    {
        var out1 = ShellRaw("settings get global bluetooth_on");
        if (string.IsNullOrEmpty(out1)) return "Could not get Bluetooth status.";
        return $"Bluetooth is {(out1.Trim() == "1" ? "on" : "off")}.";
    }

    public string AirplaneOn() { ShellRaw("settings put global airplane_mode_on 1"); ShellRaw("am broadcast -a android.intent.action.AIRPLANE_MODE --ez state true"); return "Airplane mode ON."; }
    public string AirplaneOff() { ShellRaw("settings put global airplane_mode_on 0"); ShellRaw("am broadcast -a android.intent.action.AIRPLANE_MODE --ez state false"); return "Airplane mode OFF."; }
    public string HotspotOn() { ShellRaw("svc wifi setsoftap enable"); return "Hotspot turned on."; }
    public string HotspotOff() { ShellRaw("svc wifi setsoftap disable"); return "Hotspot turned off."; }
    public string DndOn() { ShellRaw("settings put global zen_mode 2"); return "Do Not Disturb ON."; }
    public string DndOff() { ShellRaw("settings put global zen_mode 0"); return "Do Not Disturb OFF."; }

    public string FlashlightOn()
    {
        ShellRaw("cmd statusbar expand-notifications");
        Thread.Sleep(500);
        ShellRaw("input tap 540 1200");
        return "Flashlight toggled (via quick settings).";
    }

    public string FlashlightOff() => FlashlightOn();

    public string LocationOn() { ShellRaw("settings put secure location_mode 3"); return "Location turned on."; }
    public string LocationOff() { ShellRaw("settings put secure location_mode 0"); return "Location turned off."; }
    public string NfcOn() { ShellRaw("svc nfc enable"); return "NFC turned on."; }
    public string NfcOff() { ShellRaw("svc nfc disable"); return "NFC turned off."; }
    public string AutoRotateOn() { ShellRaw("settings put system accelerometer_rotation 1"); return "Auto-rotate on."; }
    public string AutoRotateOff() { ShellRaw("settings put system accelerometer_rotation 0"); return "Auto-rotate off."; }

    public string GetSettings(string category)
    {
        var cmds = new Dictionary<string, string>
        {
            ["wifi"] = "settings list global | grep -i wifi",
            ["bluetooth"] = "settings list global | grep -i bluetooth",
            ["display"] = "settings list system | grep -i -E 'screen|bright|font|rotation'",
            ["sound"] = "settings list system | grep -i -E 'volume|ring|notification|alarm'",
            ["security"] = "settings list secure | grep -i -E 'lock|screen|password'",
            ["all_global"] = "settings list global",
            ["all_system"] = "settings list system",
            ["all_secure"] = "settings list secure",
        };
        var out1 = ShellRaw(cmds.GetValueOrDefault(category, $"settings list {category}"));
        return out1.Length > 2000 ? out1[..2000] : (!string.IsNullOrEmpty(out1) ? out1 : $"No settings found for '{category}'.");
    }

    // ════════════════════════════════════════════════════════════════
    //  NETWORK
    // ════════════════════════════════════════════════════════════════

    public string IpAddress()
    {
        var out1 = Shell("ip addr show wlan0");
        foreach (var line in out1.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("inet "))
                return $"Phone IP: {line.Trim()}";
        }
        return "Could not get IP address.";
    }

    public string Ping(string host)
    {
        var out1 = ShellRaw($"ping -c 3 {host}");
        return !string.IsNullOrEmpty(out1) ? out1 : "Ping failed.";
    }

    public string WifiScan()
    {
        var out1 = ShellRaw("cmd wifi list-scan-results");
        return out1.Length > 2000 ? out1[..2000] : (!string.IsNullOrEmpty(out1) ? out1 : "No scan results.");
    }

    public string WifiInfo()
    {
        var out1 = ShellRaw("dumpsys wifi | grep -A5 'mWifiInfo'");
        return out1.Length > 1000 ? out1[..1000] : (!string.IsNullOrEmpty(out1) ? out1 : "Could not get WiFi info.");
    }

    public string ConnectedNetwork()
    {
        var out1 = ShellRaw("dumpsys connectivity | grep 'NetworkAgentInfo'");
        return out1.Length > 500 ? out1[..500] : (!string.IsNullOrEmpty(out1) ? out1 : "Could not determine connected network.");
    }

    // ════════════════════════════════════════════════════════════════
    //  CALLS & SMS
    // ════════════════════════════════════════════════════════════════

    public string MakeCall(string number) { ShellRaw($"am start -a android.intent.action.CALL -d tel:{number}"); return $"Calling {number}..."; }
    public string PhoneCall(string number) => MakeCall(number);
    public string AnswerCall() { ShellRaw("input keyevent KEYCODE_CALL"); return "Call answered."; }
    public string RejectCall() { ShellRaw("input keyevent KEYCODE_ENDCALL"); return "Call rejected."; }
    public string EndCall() { ShellRaw("input keyevent KEYCODE_ENDCALL"); return "Call ended."; }

    public string SendSms(string number, string message)
    {
        ShellRaw($"am start -a android.intent.action.SENDTO -d \"sms:{number}\" --es sms_body \"{message}\" --ez exit_on_sent true");
        return $"SMS to {number}: {message}";
    }

    public string ReadSms(int limit = 20)
    {
        var out1 = ShellRaw($"content query --uri content://sms/inbox --projection date,address,body --limit {limit}");
        if (string.IsNullOrEmpty(out1)) return "No SMS available.";
        return string.Join("\n", out1.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(20));
    }

    public string ReadNotifications()
    {
        var out1 = ShellRaw("dumpsys notification --noredact | grep -E 'pkg=|title=|text=' | head -30");
        return !string.IsNullOrEmpty(out1) ? out1 : "No notifications.";
    }

    public string ClearNotifications()
    {
        ShellRaw("service call statusbar 2");
        return "Notifications cleared.";
    }

    // ════════════════════════════════════════════════════════════════
    //  CONTACTS & CALENDAR
    // ════════════════════════════════════════════════════════════════

    public string ListContacts()
    {
        var out1 = ShellRaw("content query --uri content://com.android.contacts/contacts --projection display_name 2>/dev/null");
        if (string.IsNullOrEmpty(out1) || out1.Contains("error", StringComparison.OrdinalIgnoreCase))
            out1 = ShellRaw("dumpsys contactprovider | grep 'display_name' | head -20");
        return out1.Length > 2000 ? out1[..2000] : (!string.IsNullOrEmpty(out1) ? out1 : "Could not read contacts.");
    }

    public string SearchContacts(string name)
    {
        var out1 = ShellRaw($"content query --uri content://com.android.contacts/contacts --projection display_name --where \"display_name LIKE '%{name}%'\" | head -10");
        return !string.IsNullOrEmpty(out1) ? out1 : $"No contacts matching '{name}'.";
    }

    public string ListCalendar()
    {
        var out1 = ShellRaw("content query --uri content://com.android.calendar/events --projection title:dtstart | head -10");
        return out1.Length > 2000 ? out1[..2000] : (!string.IsNullOrEmpty(out1) ? out1 : "No calendar events found.");
    }

    // ════════════════════════════════════════════════════════════════
    //  MEDIA
    // ════════════════════════════════════════════════════════════════

    public string MediaPlay() { Shell("input keyevent KEYCODE_MEDIA_PLAY"); return "Playing."; }
    public string MediaPause() { Shell("input keyevent KEYCODE_MEDIA_PAUSE"); return "Paused."; }
    public string MediaNext() { Shell("input keyevent KEYCODE_MEDIA_NEXT"); return "Next track."; }
    public string MediaPrevious() { Shell("input keyevent KEYCODE_MEDIA_PREVIOUS"); return "Previous track."; }
    public string MediaStop() { Shell("input keyevent KEYCODE_MEDIA_STOP"); return "Media stopped."; }

    // ════════════════════════════════════════════════════════════════
    //  SYSTEM
    // ════════════════════════════════════════════════════════════════

    public string SystemInfo()
    {
        var props = new Dictionary<string, string>
        {
            ["model"] = "getprop ro.product.model",
            ["brand"] = "getprop ro.product.brand",
            ["android"] = "getprop ro.build.version.release",
            ["sdk"] = "getprop ro.build.version.sdk",
            ["security_patch"] = "getprop ro.build.version.security_patch",
            ["serial"] = "getprop ro.serialno",
            ["cpu"] = "getprop ro.hardware",
            ["ram"] = "cat /proc/meminfo | head -1",
            ["uptime"] = "cat /proc/uptime",
        };
        var lines = new List<string>();
        foreach (var kv in props)
        {
            var val = kv.Value.StartsWith("cat ", StringComparison.Ordinal) || kv.Value.StartsWith("getprop ", StringComparison.Ordinal) ? Shell(kv.Value) : ShellRaw(kv.Value);
            lines.Add($"{kv.Key}: {val}");
        }
        return string.Join("\n", lines);
    }

    public string RunningProcesses()
    {
        var out1 = ShellRaw("ps -A | head -30");
        return !string.IsNullOrEmpty(out1) ? out1 : "Could not list processes.";
    }

    public string DiskUsage()
    {
        var out1 = Shell("df -h");
        return !string.IsNullOrEmpty(out1) ? out1 : "Could not get disk usage.";
    }

    public string Logcat(int lines = 20)
    {
        var out1 = ShellRaw($"logcat -d -t {lines}");
        return out1.Length > 3000 ? out1[..3000] : (!string.IsNullOrEmpty(out1) ? out1 : "No log entries.");
    }

    public string GetProp(string prop) => Shell($"getprop {prop}");

    public string SetProp(string prop, string value)
    {
        ShellRaw($"setprop {prop} {value}");
        return $"Set {prop} = {value}.";
    }

    public string InstalledPackages()
    {
        var out1 = Shell("pm list packages -f | head -30");
        return out1.Length > 2000 ? out1[..2000] : (!string.IsNullOrEmpty(out1) ? out1 : "No packages.");
    }

    public string GrantPermission(string package, string permission) { ShellRaw($"pm grant {package} {permission}"); return $"Granted {permission} to {package}."; }
    public string RevokePermission(string package, string permission) { ShellRaw($"pm revoke {package} {permission}"); return $"Revoked {permission} from {package}."; }

    public string Notify(string title, string message)
    {
        ShellRaw($"cmd notification post -S bigtext -t \"{title}\" \"ultron\" \"{message}\" 2>/dev/null");
        return $"Notification sent: {title} — {message}";
    }

    public string RingPhone()
    {
        ShellRaw("input keyevent KEYCODE_MEDIA_PLAY_PAUSE");
        return "Attempted to ring phone.";
    }

    public string Vibrate(int ms = 500)
    {
        ShellRaw($"input vibrationtime {ms}");
        return $"Vibrated {ms}ms.";
    }

    // ════════════════════════════════════════════════════════════════
    //  UTILITY
    // ════════════════════════════════════════════════════════════════

    public static void PlayCompletionSound()
    {
        try
        {
            Console.Beep(800, 150);
            Thread.Sleep(100);
            Console.Beep(1000, 150);
            Thread.Sleep(100);
            Console.Beep(1200, 200);
        }
        catch { }
    }
}