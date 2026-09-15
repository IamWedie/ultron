using System.Runtime.InteropServices;
using System.Text;

namespace Ultron.Services;

/// <summary>
/// System-tray icon and its hidden Win32 message window.  The hidden window also
/// receives WM_HOTKEY from <see cref="HotkeyService"/> and WM_APP+1 tray callbacks,
/// so the app can live entirely in the tray.  Pure Win32 (no package dependency).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint NifMessage = 0x01, NifIcon = 0x02, NifTip = 0x04, NifInfo = 0x10, NifGuid = 0x20;
    private const uint NiiInfo = 0x1;
    private const uint WmApp = 0x8000, WmLButtonUp = 0x0202, WmRButtonUp = 0x0205;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint ImageIcon = 2, LrLoadFromFile = 0x00000010, LrDefaultSize = 0x00000040;
    private const uint MfString = 0x0, MfSeparator = 0x800;
    private const int TpmRightButton = 0x2, TpmReturnCmd = 0x100;

    private const int CmdShow = 1, CmdAwake = 2, CmdMute = 3, CmdPtt = 4, CmdDeck = 6, CmdQuit = 5;

    /// <summary>Fixed identity for the notification icon so the shell reuses a
    /// single slot across restarts (no stale ghosts pointing at dead hwnds).</summary>
    private static readonly Guid TrayGuid = new("0D89B8A6-AA79-44D2-BDB3-6D392B4F62A1");

    private readonly IntPtr _hwnd;
    private readonly IntPtr _hIcon;
    private readonly WndProcDelegate _wndProc;
    private readonly string _tip = "ULTRON — voice assistant";

    public IntPtr WindowHandle => _hwnd;

    public event Action? ShowRequested;
    public event Action? ToggleAwakeRequested;
    public event Action? ToggleMuteRequested;
    public event Action? PushToTalkRequested;
    public event Action? DashboardRequested;
    public event Action? QuitRequested;
    public event Action<int>? HotKeyPressed;

    public TrayIcon()
    {
        _wndProc = WndProc;
        _hwnd = CreateHiddenWindow(_wndProc);
        _hIcon = LoadTrayIcon();
        AddIcon();
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmApp) // tray callback message
        {
            var param = (uint)lParam.ToInt64() & 0xFFFF;
            AppLog.Write("TrayIcon", $"tray callback: {param:x4} on hwnd 0x{hWnd.ToInt64():x}");
            switch (param)
            {
                case WmLButtonUp:
                    ShowRequested?.Invoke();
                    break;
                case WmRButtonUp:
                    var p = new POINT();
                    GetCursorPos(ref p);
                    ShowMenu(p);
                    break;
            }
            return IntPtr.Zero;
        }
        if (msg == 0x0312) // WM_HOTKEY
        {
            AppLog.Write("TrayIcon", $"WM_HOTKEY id={wParam} on hwnd 0x{hWnd.ToInt64():x}");
            HotKeyPressed?.Invoke(checked((int)wParam));
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu(POINT p)
    {
        var menu = CreatePopupMenu();
        AppLog.Write("TrayIcon", $"ShowMenu: at ({p.x},{p.y}) menu=0x{menu.ToInt64():x}");
        AppendMenuW(menu, MfString, CmdShow, "Show / Hide ULTRON");
        AppendMenuW(menu, MfString, CmdAwake, "Wake Toggle");
        AppendMenuW(menu, MfString, CmdMute, "Mute Mic");
        AppendMenuW(menu, MfString, CmdPtt, "Push-to-Talk");
        AppendMenuW(menu, MfString, CmdDeck, "Web Deck (QR code)…");
        AppendMenuW(menu, MfSeparator, IntPtr.Zero, null);
        AppendMenuW(menu, MfString, CmdQuit, "Quit");

        var cmd = TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd, p.x, p.y, 0, _hwnd, IntPtr.Zero);
        AppLog.Write("TrayIcon", $"ShowMenu: TrackPopupMenu → {(int)cmd} (err={Marshal.GetLastWin32Error()})");
        DestroyMenu(menu);
        switch (checked((int)cmd))
        {
            case CmdShow: ShowRequested?.Invoke(); break;
            case CmdAwake: ToggleAwakeRequested?.Invoke(); break;
            case CmdMute: ToggleMuteRequested?.Invoke(); break;
            case CmdPtt: PushToTalkRequested?.Invoke(); break;
            case CmdDeck: DashboardRequested?.Invoke(); break;
            case CmdQuit: QuitRequested?.Invoke(); break;
        }
    }

    public void ShowBalloon(string title, string body, bool error = false)
    {
        var data = NewNotifyData();
        data.hWnd = _hwnd;
        data.uID = 1;
        data.hIcon = _hIcon;
        data.uTimeout = 5000;
        data.dwInfoFlags = error ? 0x3u : NiiInfo;
        SetString(data.szInfo, body, 256);
        SetString(data.szInfoTitle, title, 64);
        data.uFlags = NifMessage | NifIcon | NifInfo;
        Shell_NotifyIconW(NimModify, ref data);
    }

    public void SetTooltip(string tip)
    {
        var data = NewNotifyData();
        data.hWnd = _hwnd;
        data.uID = 1;
        SetString(data.szTip, tip, 128);
        data.uFlags = NifTip;
        Shell_NotifyIconW(NimModify, ref data);
    }

    private void AddIcon()
    {
        var data = NewNotifyData();
        data.hWnd = _hwnd;
        data.uID = 1;
        data.uFlags = NifMessage | NifIcon | NifTip | NifGuid;
        data.uCallbackMessage = WmApp;
        data.hIcon = _hIcon;
        SetString(data.szTip, _tip, 128);
        Shell_NotifyIconW(NimAdd, ref data);

        var ver = NewNotifyData();
        ver.hWnd = _hwnd;
        ver.uID = 1;
        ver.uTimeout = 4; // NOTIFYICON_VERSION_4
        Shell_NotifyIconW(NimSetVersion, ref ver);
    }

    private static NOTIFYICONDATAW NewNotifyData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        guidItem = TrayGuid,
        szTip = new char[128],
        szInfo = new char[256],
        szInfoTitle = new char[64],
    };

    private static void SetString(char[] buffer, string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return;
        var chars = value[..Math.Min(value.Length, max - 1)];
        chars.CopyTo(0, buffer, 0, chars.Length);
    }

    private static IntPtr LoadTrayIcon()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "UltronTray.ico"),
            Path.Combine(AppContext.BaseDirectory, "Ultron.ico"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "Ultron.ico"),
        };
        foreach (var ico in candidates)
        {
            if (File.Exists(ico))
            {
                var h = LoadImageW(IntPtr.Zero, ico, ImageIcon, 0, 0, LrLoadFromFile | LrDefaultSize);
                if (h != IntPtr.Zero) return h;
            }
        }
        return LoadIconW(IntPtr.Zero, (IntPtr)0x7F00); // IDI_APPLICATION
    }

    private static IntPtr CreateHiddenWindow(WndProcDelegate wndProc)
    {
        var hInstance = GetModuleHandleW(null);
        var className = "UltronTrayWindowV1";

        var wc = new WNDCLASSEXW
        {
            cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = wndProc,
            hInstance = hInstance,
            lpszClassName = className,
        };
        RegisterClassExW(ref wc);

        return CreateWindowExW(0, className, "UltronTray", 0,
            int.MinValue, int.MinValue, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    public void Dispose()
    {
        var data = NewNotifyData();
        data.hWnd = _hwnd;
        data.uID = 1;
        Shell_NotifyIconW(NimDelete, ref data);
        if (_hIcon != IntPtr.Zero && _hIcon != (IntPtr)0x7F00)
            DestroyIcon(_hIcon);
        DestroyWindow(_hwnd);
    }

    // ─────────────────────────── Win32 interop ───────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public int cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public char[] szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public char[] szInfo;
        public uint uTimeout;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public char[] szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent,
        IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpdata);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hinst, string lpszName, uint type,
        int cx, int cy, uint fuLoad);
    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
        int nReserved, IntPtr hWnd, IntPtr prcRect);
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(ref POINT lpPoint);
}