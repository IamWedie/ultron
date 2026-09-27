// MainWindow.Native.cs - SendInput P/Invoke structs and the Native shim
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
using QRCoder;

namespace Ultron;

public sealed partial class MainWindow
{
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
