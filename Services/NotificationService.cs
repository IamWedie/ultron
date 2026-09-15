using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Ultron.Services;

/// <summary>
/// Native toast notifications (Windows App SDK) with a tray-balloon fallback
/// when toasts are unavailable (e.g. first launch before the AUMID shortcut
/// exists, or when the package/app-id registration fails).
/// </summary>
public sealed class NotificationService : IDisposable
{
    public const string Aumid = "Ultron.UltronAssistant";

    private readonly TrayIcon? _tray;
    private bool _toastReady;

    public NotificationService(TrayIcon? tray)
    {
        _tray = tray;
        InitToasts();
    }

    private void InitToasts()
    {
        try
        {
            AppNotificationManager.Default.Register();
            _toastReady = true;
        }
        catch
        {
            _toastReady = false;
        }
    }

    public void Notify(string title, string body)
    {
        if (_toastReady)
        {
            try
            {
                var builder = new AppNotificationBuilder().AddText(title);
                if (!string.IsNullOrEmpty(body))
                    builder.AddText(body);
                AppNotificationManager.Default.Show(builder.BuildNotification());
                return;
            }
            catch
            {
                // Fall through to the balloon.
            }
        }

        _tray?.ShowBalloon(title, body);
    }

    /// <summary>
    /// Registers the Start-menu shortcut that carries our AppUserModelID so
    /// unpackaged toast notifications have a stable identity.  Idempotent.
    /// </summary>
    public static void EnsureAumidShortcut()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            var startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs");
            var lnk = Path.Combine(startMenu, "ULTRON.lnk");
            if (File.Exists(lnk)) return;

            Directory.CreateDirectory(startMenu);
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory);
            link.SetIconLocation(exe, 0);
            ((IPersistFile)link).Save(lnk, true);

            SetShortcutAumid(link, Aumid);
        }
        catch
        {
            // Toasts fall back to the tray balloon; not fatal.
        }
    }

    private static void SetShortcutAumid(IShellLinkW link, string aumid)
    {
        var props = (IPropertyStore)link;
        var pkey = new PROPERTYKEY(AppUserModelPropSet, 5);
        var pv = new PROPVARIANT();
        pv.vt = VtLpwStr;
        pv.pwszVal = Marshal.StringToCoTaskMemUni(aumid);
        try
        {
            props.SetValue(pkey, pv);
            props.Commit();
        }
        finally
        {
            Marshal.FreeCoTaskMem(pv.pwszVal);
            Marshal.ReleaseComObject(props);
        }
    }

    public void Dispose()
    {
        try
        {
            if (_toastReady) AppNotificationManager.Default.Unregister();
        }
        catch
        {
        }
    }

    private static readonly Guid AppUserModelPropSet = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const ushort VtLpwStr = 31;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
        public PROPERTYKEY(Guid fmt, uint p) { fmtid = fmt; pid = p; }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort wReserved1;
        [FieldOffset(4)] public ushort wReserved2;
        [FieldOffset(6)] public ushort wReserved3;
        [FieldOffset(8)] public IntPtr pwszVal;
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        [PreserveSig] int GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(PROPERTYKEY key, PROPVARIANT pv);
        void Commit();
    }
}