# Control of the default audio output device via the Windows Core Audio API.
# Run directly:  .\AudioControl.ps1 list
# Or dot-source it to get [AudioNative].
param(
    [ValidateSet('list')]
    [string]$Action
)

if (-not ('AudioNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class AudioDevice {
    public string Id;
    public string Name;
    public bool IsDefault;
    public bool Disconnected;
    public override string ToString() { return Name; }
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator {
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection {
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int Item(int index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice {
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore {
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
    [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
}

// Undocumented but stable since Windows 7; also used by the Windows sound settings.
[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPolicyConfig {
    [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
    [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
    [PreserveSig] int ResetDeviceFormat(IntPtr a);
    [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
    [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
    [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
    [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
}

[StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
[StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p1; public IntPtr p2; }

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume {
    [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
    [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom {}
[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] class PolicyConfigCom {}

public static class AudioNative {
    public const int Output = 0, Input = 1;   // EDataFlow: eRender, eCapture
    const int eRender = 0;
    const int eConsole = 0, eMultimedia = 1, eCommunications = 2;
    const int CLSCTX_ALL = 23;
    static readonly Guid VolumeIid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
    const int DEVICE_STATE_ACTIVE = 1;
    const ushort VT_LPWSTR = 31;
    static readonly Guid FriendlyNameFmt = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    static IMMDeviceEnumerator Enumerator() { return (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); }

    static string GetName(IMMDevice device) {
        IPropertyStore store;
        if (device.OpenPropertyStore(0, out store) != 0) return null;
        PROPERTYKEY key = new PROPERTYKEY(); key.fmtid = FriendlyNameFmt; key.pid = 14;
        PROPVARIANT pv;
        if (store.GetValue(ref key, out pv) != 0) return null;
        string name = pv.vt == VT_LPWSTR ? Marshal.PtrToStringUni(pv.p1) : null;
        PropVariantClear(ref pv);
        return name;
    }

    public static string GetDefaultId(int flow) {
        IMMDevice device;
        if (Enumerator().GetDefaultAudioEndpoint(flow, eConsole, out device) != 0) return null;
        string id; device.GetId(out id);
        return id;
    }

    public static string GetDefaultName(int flow) {
        IMMDevice device;
        if (Enumerator().GetDefaultAudioEndpoint(flow, eConsole, out device) != 0) return null;
        return GetName(device);
    }

    public static string GetDefaultOutputId() { return GetDefaultId(Output); }
    public static string GetDefaultOutputName() { return GetDefaultName(Output); }
    public static List<AudioDevice> GetOutputDevices() { return GetDevices(Output); }
    public static void SetDefaultOutput(string id) { SetDefault(id, false); }

    public static List<AudioDevice> GetDevices(int flow) {
        List<AudioDevice> result = new List<AudioDevice>();
        string defaultId = GetDefaultId(flow);
        IMMDeviceCollection devices;
        if (Enumerator().EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, out devices) != 0) return result;
        int count; devices.GetCount(out count);
        for (int i = 0; i < count; i++) {
            IMMDevice device;
            if (devices.Item(i, out device) != 0) continue;
            AudioDevice d = new AudioDevice();
            device.GetId(out d.Id);
            d.Name = GetName(device) ?? d.Id;
            d.IsDefault = d.Id == defaultId;
            result.Add(d);
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    // Sets the device as default for games/media (like "Set as default" in Windows);
    // microphones also become the default for voice chat (communications)
    public static void SetDefault(string id, bool includeCommunications) {
        IPolicyConfig policy = (IPolicyConfig)new PolicyConfigCom();
        int err = policy.SetDefaultEndpoint(id, eConsole);
        if (err == 0) err = policy.SetDefaultEndpoint(id, eMultimedia);
        if (err == 0 && includeCommunications) err = policy.SetDefaultEndpoint(id, eCommunications);
        if (err != 0) throw new Exception("Could not switch the audio device (error 0x" + err.ToString("X8") + ").");
    }

    static IAudioEndpointVolume DefaultOutputVolume() {
        IMMDevice device;
        if (Enumerator().GetDefaultAudioEndpoint(eRender, eConsole, out device) != 0) return null;
        Guid iid = VolumeIid;
        object o;
        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o) != 0) return null;
        return (IAudioEndpointVolume)o;
    }

    // Master volume of the default output device, 0..100 (-1 if unavailable)
    public static int GetVolume() {
        IAudioEndpointVolume v = DefaultOutputVolume();
        float level;
        if (v == null || v.GetMasterVolumeLevelScalar(out level) != 0) return -1;
        return (int)Math.Round(level * 100);
    }

    public static void SetVolume(int percent) {
        IAudioEndpointVolume v = DefaultOutputVolume();
        if (v == null) throw new Exception("No audio output device.");
        Guid ctx = Guid.Empty;
        int err = v.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(100, percent)) / 100f, ref ctx);
        if (err != 0) throw new Exception("Could not set the volume (error 0x" + err.ToString("X8") + ").");
    }
}
'@
}

if ($Action -eq 'list') {
    'Output:'; [AudioNative]::GetDevices([AudioNative]::Output) | Format-Table Name, IsDefault, Id -AutoSize
    'Input:'; [AudioNative]::GetDevices([AudioNative]::Input) | Format-Table Name, IsDefault, Id -AutoSize
    "Volume: $([AudioNative]::GetVolume()) %"
}
