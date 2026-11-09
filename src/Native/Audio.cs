using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GameSwitcher.Native
{
    class AudioDevice
    {
        public string Id, Name;
        public bool IsDefault;
    }

    // Default audio devices and volume via the Windows Core Audio API
    static class Audio
    {
        public const int Output = 0, Input = 1;   // EDataFlow: eRender, eCapture

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        }

        // Undocumented but stable since Windows 7; also used by the Windows sound settings
        [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPolicyConfig
        {
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
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
            [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        }

        [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
        [StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p1; public IntPtr p2; }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }
        [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] class PolicyConfigCom { }

        const int eConsole = 0, eMultimedia = 1, eCommunications = 2;
        const int DEVICE_STATE_ACTIVE = 1;
        const int CLSCTX_ALL = 23;
        const ushort VT_LPWSTR = 31;
        static readonly Guid FriendlyNameFmt = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
        static readonly Guid VolumeIid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

        static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();

        static string GetName(IMMDevice device)
        {
            if (device.OpenPropertyStore(0, out IPropertyStore store) != 0) return null;
            var key = new PROPERTYKEY { fmtid = FriendlyNameFmt, pid = 14 };
            if (store.GetValue(ref key, out PROPVARIANT pv) != 0) return null;
            string name = pv.vt == VT_LPWSTR ? Marshal.PtrToStringUni(pv.p1) : null;
            PropVariantClear(ref pv);
            return name;
        }

        static IMMDevice DefaultDevice(int flow) =>
            Enumerator().GetDefaultAudioEndpoint(flow, eConsole, out IMMDevice device) == 0 ? device : null;

        public static string GetDefaultId(int flow)
        {
            IMMDevice device = DefaultDevice(flow);
            if (device == null) return null;
            device.GetId(out string id);
            return id;
        }

        public static string GetDefaultName(int flow)
        {
            IMMDevice device = DefaultDevice(flow);
            return device == null ? null : GetName(device);
        }

        public static List<AudioDevice> GetDevices(int flow)
        {
            var result = new List<AudioDevice>();
            string defaultId = GetDefaultId(flow);
            if (Enumerator().EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, out IMMDeviceCollection devices) != 0) return result;
            devices.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (devices.Item(i, out IMMDevice device) != 0) continue;
                device.GetId(out string id);
                result.Add(new AudioDevice { Id = id, Name = GetName(device) ?? id, IsDefault = id == defaultId });
            }
            return result;
        }

        public static bool IsConnected(int flow, string id) => GetDevices(flow).Exists(d => d.Id == id);

        // Sets the device as default for games/media (like "Set as default" in Windows);
        // microphones also become the default for voice chat (communications)
        public static void SetDefault(string id, bool includeCommunications)
        {
            var policy = (IPolicyConfig)new PolicyConfigCom();
            int err = policy.SetDefaultEndpoint(id, eConsole);
            if (err == 0) err = policy.SetDefaultEndpoint(id, eMultimedia);
            if (err == 0 && includeCommunications) err = policy.SetDefaultEndpoint(id, eCommunications);
            if (err != 0) throw new Exception("Could not switch the audio device (error 0x" + err.ToString("X8") + ").");
        }

        static IAudioEndpointVolume DefaultOutputVolume()
        {
            IMMDevice device = DefaultDevice(Output);
            if (device == null) return null;
            Guid iid = VolumeIid;
            return device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object o) == 0 ? (IAudioEndpointVolume)o : null;
        }

        // Master volume of the default output device, 0..100 (-1 if unavailable)
        public static int GetVolume()
        {
            IAudioEndpointVolume v = DefaultOutputVolume();
            if (v == null || v.GetMasterVolumeLevelScalar(out float level) != 0) return -1;
            return (int)Math.Round(level * 100);
        }

        public static void SetVolume(int percent)
        {
            IAudioEndpointVolume v = DefaultOutputVolume() ?? throw new Exception("No audio output device.");
            Guid ctx = Guid.Empty;
            int err = v.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(100, percent)) / 100f, ref ctx);
            if (err != 0) throw new Exception("Could not set the volume (error 0x" + err.ToString("X8") + ").");
        }
    }
}
