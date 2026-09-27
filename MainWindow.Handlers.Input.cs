// MainWindow.Handlers.Input.cs - type_text, press_key, window tracking
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

public sealed partial class MainWindow
{
    private async Task<ToolResult> HandleTypeTextAsync(Dictionary<string, object> args)
    {
        var text = args.GetValueOrDefault("text")?.ToString() ?? "";
        if (string.IsNullOrEmpty(text)) return ToolResult.InvalidArguments("type_text needs a non-empty 'text' argument.");
        // Resolve where the user actually wants the text typed.
        var target = ResolveTypeTarget();
        if (target == IntPtr.Zero)
            return ToolResult.Fail("No active window found to type into — click a window first, then try again.");
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
            return ToolResult.Ok($"Typed: {text}" + (string.IsNullOrEmpty(targetName) ? "" : $" (into \"{targetName}\")"));
        }
        catch (Exception ex) { return ToolResult.Fail($"Could not type: {ex.Message}"); }
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

    private ToolResult HandlePressKey(Dictionary<string, object> args)
    {
        var spec = args.GetValueOrDefault("keys")?.ToString()
                   ?? args.GetValueOrDefault("key")?.ToString() ?? "";
        var repeat = args.GetValueOrDefault("repeat") is JsonElement je && je.TryGetInt32(out var rr) ? rr : 1;
        if (string.IsNullOrEmpty(spec)) return ToolResult.InvalidArguments("press_key needs a 'keys' argument, e.g. \"ctrl+s\".");
        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return ToolResult.InvalidArguments("press_key needs a 'keys' argument, e.g. \"ctrl+s\".");
        var codes = parts.Select(LookupVk).ToList();
        var bad = parts.Zip(codes, (p, c) => c == 0 ? p : null).FirstOrDefault(n => n != null);
        if (bad != null) return ToolResult.InvalidArguments($"Unrecognized key: {bad}. Supported: letters, digits, enter, tab, backspace, delete, escape, arrows, f1-f12, ctrl/alt/shift/win combos.");
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
            return ToolResult.Ok($"Pressed {spec}" + (repeat > 1 ? $" x{repeat}" : "") + ".");
        }
        catch (Exception ex) { return ToolResult.Fail($"Could not press {spec}: {ex.Message}"); }
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

}
