using System;
using System.Runtime.InteropServices;

namespace Ultron.Services;

/// <summary>Reads/writes the master volume + mute via the Windows Core Audio API,
/// so undo can restore the exact value that was there before a change.</summary>
internal static class SystemVolume
{
    private const uint EDataFlow_Render = 0;
    private const uint ERole_Console = 0;
    private const uint CLSCTX_INPROC_SERVER = 0x1;

    private static readonly Guid IID_IMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary>Returns (-1, false) if the device or interface cannot be reached.</summary>
    public static (float volume, bool muted) Get()
    {
        try
        {
            var device = GetDefaultEndpoint();
            if (device == null) return (-1f, false);
            try
            {
                var vol = ActivateVolume(device);
                if (vol == null) return (-1f, false);
                try
                {
                    float level;
                    if (vol.GetMasterVolumeLevelScalar(out level) != 0) return (-1f, false);
                    bool muted;
                    vol.GetMute(out muted);
                    return (level, muted);
                }
                finally { Marshal.ReleaseComObject(vol); }
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        catch { return (-1f, false); }
    }

    public static void Set(float volume, bool muted)
    {
        try
        {
            var device = GetDefaultEndpoint();
            if (device == null) return;
            try
            {
                var vol = ActivateVolume(device);
                if (vol == null) return;
                try
                {
                    var ctx = Guid.Empty;
                    if (volume >= 0f && volume <= 1f)
                        vol.SetMasterVolumeLevelScalar(volume, ref ctx);
                    vol.SetMute(muted, ref ctx);
                }
                finally { Marshal.ReleaseComObject(vol); }
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        catch { }
    }

    private static IMMDeviceEnumerator NewEnumerator()
    {
        var iid = IID_IMMDeviceEnumerator;
        object inst;
        var hr = CoCreateInstance(CLSID_MMDeviceEnumerator, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out inst);
        if (hr != 0) throw new COMException("MMDeviceEnumerator failed", hr);
        return (IMMDeviceEnumerator)inst;
    }

    private static IMMDevice? GetDefaultEndpoint()
    {
        var em = NewEnumerator();
        try
        {
            var hr = em.GetDefaultAudioEndpoint(EDataFlow_Render, ERole_Console, out var device);
            if (hr != 0 || device == null) return null;
            return device;
        }
        finally { Marshal.ReleaseComObject(em); }
    }

    private static IAudioEndpointVolume? ActivateVolume(IMMDevice device)
    {
        var iid = IID_IAudioEndpointVolume;
        object obj;
        var hr = device.Activate(ref iid, CLSCTX_INPROC_SERVER, IntPtr.Zero, out obj);
        if (hr != 0 || obj == null) return null;
        return (IAudioEndpointVolume)obj;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        [MarshalAs(UnmanagedType.LPStruct)] Guid clsid,
        IntPtr punkOuter, uint dwClsContext,
        [MarshalAs(UnmanagedType.LPStruct)] ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(uint dataFlow, uint dwStateMask, out IMMDeviceCollection ppDevices);
    [PreserveSig] int GetDefaultAudioEndpoint(uint dataFlow, uint role, out IMMDevice ppDevice);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient pClient);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
    [PreserveSig] int GetState(out uint pdwState);
}

[ComImport]
[Guid("1BE09788-6894-4089-8586-9A2A6C265AC5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint pcDevices);
    [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
}

// IAudioEndpointVolume vtable (after IUnknown): 3 RegisterControlChangeNotify,
// 4 Unregister, 5 GetChannelCount, 6 SetMasterVolumeLevel, 7 SetMasterVolumeLevelScalar,
// 8 GetMasterVolumeLevel, 9 GetMasterVolumeLevelScalar, 10 SetChannelVolumeLevel,
// 11 SetChannelVolumeLevelScalar, 12 GetChannelVolumeLevel, 13 GetChannelVolumeLevelScalar,
// 14 SetMute, 15 GetMute.
[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
    [PreserveSig] int GetChannelCount(out uint pnChannelCount);
    [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
    [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
    [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
    [PreserveSig] int SetMute(bool bMute, ref Guid pguidEventContext);
    [PreserveSig] int GetMute(out bool pbMute);
}

[ComImport]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnDefaultDeviceChanged(uint dataFlow, uint role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref int key);
}