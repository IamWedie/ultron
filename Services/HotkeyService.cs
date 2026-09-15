using System.Runtime.InteropServices;
using System.Text;

namespace Ultron.Services;

/// <summary>
/// Global hotkeys routed through the tray icon's hidden Win32 window.
/// Pressing a registered combo surfaces <see cref="Pressed"/> on the UI thread.
/// Bindings are replaceable at runtime so the user can reconfigure them.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly TrayIcon _tray;
    private readonly Dictionary<int, (uint Mods, uint Vk)> _bindings = new();
    private bool _subscribed;

    public event Action<int>? Pressed;

    public HotkeyService(TrayIcon tray)
    {
        _tray = tray;
        tray.HotKeyPressed += OnHotKey;
        _subscribed = true;
    }

    private void OnHotKey(int id) => Pressed?.Invoke(id);

    /// <param name="id">Our identifier (1-0xBFFF per Win32).</param>
    public bool Register(int id, uint modifiers, uint vk)
    {
        if (!_bindings.ContainsKey(id) &&
            RegisterHotKey(_tray.WindowHandle, id, modifiers, vk))
        {
            _bindings[id] = (modifiers, vk);
            return true;
        }
        return false;
    }

    public bool Unregister(int id)
    {
        if (_bindings.Remove(id))
        {
            UnregisterHotKey(_tray.WindowHandle, id);
            return true;
        }
        return false;
    }

    /// <summary>Swap a binding from a "Win+Alt+P"-style spec. On an invalid spec
    /// the existing binding (if any) is left untouched and false is returned.</summary>
    public bool UpdateBinding(int id, string spec)
    {
        if (!TryParse(spec, out var mods, out var vk)) return false;
        Unregister(id);
        return Register(id, mods, vk);
    }

    /// <summary>Parse a hotkey spec like "Win+Alt+P" or "Ctrl+F9".
    /// Modifiers: Win, Ctrl/Control, Alt, Shift. Final token: A-Z, 0-9, or F1-F12.</summary>
    public static bool TryParse(string spec, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(spec)) return false;
        var tokens = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2) return false;
        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (i == tokens.Length - 1)
            {
                if (t.Length == 1 && char.IsLetterOrDigit(t[0])) { vk = char.ToUpperInvariant(t[0]); continue; }
                if (t.Length == 2 && (t[0] == 'F' || t[0] == 'f') && char.IsDigit(t[1]))
                {
                    var n = t[1] - '0';
                    if (n >= 1) { vk = (uint)(0x70 + n - 1); continue; } // F1=0x70
                    return false;
                }
                if (t.StartsWith("F", StringComparison.OrdinalIgnoreCase) && t.Length == 3 &&
                    char.IsDigit(t[1]) && char.IsDigit(t[2]))
                {
                    var n = (t[1] - '0') * 10 + (t[2] - '0');
                    if (n is >= 1 and <= 12) { vk = (uint)(0x70 + n - 1); continue; }
                    return false;
                }
                return false;
            }

            switch (t.ToLowerInvariant())
            {
                case "win" when (mods & ModWin) == 0: mods |= ModWin; break;
                case "ctrl" when (mods & ModControl) == 0: mods |= ModControl; break;
                case "control" when (mods & ModControl) == 0: mods |= ModControl; break;
                case "alt" when (mods & ModAlt) == 0: mods |= ModAlt; break;
                case "shift" when (mods & ModShift) == 0: mods |= ModShift; break;
                default: return false;
            }
        }
        return vk != 0;
    }

    /// <summary>Render a spec for display, e.g. (ModWin|ModAlt, 'P' + 0x30?) → "Win+Alt+P".</summary>
    public static string ToDisplay(uint mods, uint vk)
    {
        var sb = new StringBuilder();
        if ((mods & ModWin) != 0) sb.Append("Win+");
        if ((mods & ModControl) != 0) sb.Append("Ctrl+");
        if ((mods & ModAlt) != 0) sb.Append("Alt+");
        if ((mods & ModShift) != 0) sb.Append("Shift+");
        if (vk >= 0x70 && vk <= 0x7B) sb.Append('F').Append(vk - 0x70 + 1);
        else if (vk >= 0x30 && vk <= 0x39) sb.Append((char)vk);
        else if (vk >= 0x41 && vk <= 0x5A) sb.Append((char)vk);
        else sb.Append('#').Append(vk);
        return sb.ToString();
    }

    public void Dispose()
    {
        foreach (var id in _bindings.Keys)
            UnregisterHotKey(_tray.WindowHandle, id);
        _bindings.Clear();
        if (_subscribed)
        {
            _tray.HotKeyPressed -= OnHotKey;
            _subscribed = false;
        }
    }

    // Common modifier bits
    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    // Virtual-key codes we care about
    public const uint VkP = 0x50, VkM = 0x4D, VkK = 0x4B, VkL = 0x4C;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}