// MainWindow.Handlers.Desktop.cs - desktop_control, window_manage, mouse helpers
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
    private async Task<ToolResult> HandleDesktopControl(Dictionary<string, object> args)
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
                    return await RunAsyncTool("snippingtool", "/clip");

                case "move_mouse":
                case "mouse_move":
                    MoveMouse(x, y);
                    return ToolResult.Ok($"Moved mouse to ({x ?? CurrentX()}, {y ?? CurrentY()}).");

                case "click":
                    ClickAt(x, y);
                    return ToolResult.Ok($"Clicked at ({x ?? CurrentX()}, {y ?? CurrentY()}).");

                case "double_click":
                    ClickAt(x, y, doubleClick: true);
                    return ToolResult.Ok($"Double-clicked at ({x ?? CurrentX()}, {y ?? CurrentY()}).");

                case "right_click":
                    RightClickAt(x, y);
                    return ToolResult.Ok($"Right-clicked at ({x ?? CurrentX()}, {y ?? CurrentY()}).");

                case "scroll":
                    Scroll(amount ?? 1);
                    return ToolResult.Ok($"Scrolled {(amount ?? 1)} notch(es).");

                case "focus_window":
                    MoveMouse(x, y);
                    return ToolResult.Ok("Brought the active window to the foreground.");
            }
        }
        catch (Exception e)
        {
            return ToolResult.Fail($"Desktop action '{action}' failed: {e.Message}");
        }
        return ToolResult.Unsupported(
            $"Desktop action '{action}' is not implemented. Supported: screenshot, move_mouse, click, double_click, right_click, scroll, focus_window.");
    }

    private ToolResult HandleWindowManage(Dictionary<string, object> args)
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
                        return ToolResult.NotFound($"No window found for '{app}{(!string.IsNullOrEmpty(title) ? title : "")}'.");
                    Native.FocusWindow(h);
                    return ToolResult.Ok($"Focused \"{Native.WindowTitle(h)}\".");
                }

                case "minimize":
                {
                    var fg = Native.GetForegroundWindow();
                    if (fg != IntPtr.Zero && fg != _selfHwnd) { Native.ShowWindow(fg, Native.SW_MINIMIZE); return ToolResult.Ok("Minimized the active window."); }
                    return ToolResult.Fail("Nothing to minimize: no other window is in the foreground.");
                }
                case "maximize":
                {
                    var fg = Native.GetForegroundWindow();
                    if (fg != IntPtr.Zero && fg != _selfHwnd) { Native.ShowWindow(fg, Native.SW_MAXIMIZE); return ToolResult.Ok("Maximized the active window."); }
                    return ToolResult.Fail("Nothing to maximize: no other window is in the foreground.");
                }
                case "restore":
                {
                    var fg = Native.GetForegroundWindow();
                    if (fg != IntPtr.Zero && fg != _selfHwnd) { Native.ShowWindow(fg, Native.SW_RESTORE); return ToolResult.Ok("Restored the active window."); }
                    return ToolResult.Fail("Nothing to restore: no other window is in the foreground.");
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
                    return ToolResult.Ok("Sent Alt+F4 — closing the active window.");
                case "next_window":
                    SendInputBatch(new List<NativeInput>
                    {
                        KeyCodeInput(0x12, false, unicode: false),  // Alt
                        KeyCodeInput(0x09, false, unicode: false),  // Tab
                        KeyCodeInput(0x09, true, unicode: false),
                        KeyCodeInput(0x12, true, unicode: false),
                    });
                    return ToolResult.Ok("Switched to the next window.");
                case "show_desktop":
                    SendInputBatch(new List<NativeInput>
                    {
                        KeyCodeInput(0x5B, false, unicode: false),  // Win
                        KeyCodeInput(0x44, false, unicode: false),  // D
                        KeyCodeInput(0x44, true, unicode: false),
                        KeyCodeInput(0x5B, true, unicode: false),
                    });
                    return ToolResult.Ok("Showed the desktop.");
                case "list":
                case "list_windows":
                    return ToolResult.Ok(Native.ListWindows());
            }
            return ToolResult.Unsupported($"Window action '{action}' not supported. Try: focus_app, minimize, maximize, restore, close, next_window, show_desktop, list_windows.");
        }
        catch (Exception e)
        {
            return ToolResult.Fail($"Window action '{action}' failed: {e.Message}");
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

}
