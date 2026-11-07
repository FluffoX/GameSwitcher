# HDR control via the Windows DisplayConfig API.
# Run directly:  .\HdrControl.ps1 status | on | off
# Or dot-source it to get Get-HdrDisplays / Set-Hdr.
param(
    [ValidateSet('status', 'on', 'off')]
    [string]$Action
)

if (-not ('HdrNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class HdrDisplay {
    public string Name;
    public uint AdapterLow;
    public int AdapterHigh;
    public uint TargetId;
    public bool HdrSupported;
    public bool HdrEnabled;
    public bool UsesNewApi;
    public string Key { get { return AdapterHigh + ":" + AdapterLow + ":" + TargetId; } }
}

public static class HdrNative {
    [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] public struct PATH_SOURCE { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)] public struct PATH_TARGET {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
        public uint rotation; public uint scaling; public uint refreshNum; public uint refreshDen;
        public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] public struct PATH_INFO { public PATH_SOURCE source; public PATH_TARGET target; public uint flags; }
    [StructLayout(LayoutKind.Explicit, Size = 64)] public struct MODE_INFO { [FieldOffset(0)] public uint infoType; }
    [StructLayout(LayoutKind.Sequential)] public struct HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential)] public struct COLOR_INFO { public HEADER header; public uint value; public uint colorEncoding; public uint bitsPerColorChannel; }
    [StructLayout(LayoutKind.Sequential)] public struct COLOR_INFO_2 { public HEADER header; public uint value; public uint colorEncoding; public uint bitsPerColorChannel; public uint activeColorMode; }
    [StructLayout(LayoutKind.Sequential)] public struct SET_STATE { public HEADER header; public uint value; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct TARGET_NAME {
        public HEADER header; public uint flags; public uint outputTechnology;
        public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string friendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string devicePath;
    }

    const uint QDC_ONLY_ACTIVE_PATHS = 2;
    const uint GET_TARGET_NAME = 2;
    const uint GET_ADVANCED_COLOR_INFO = 9;
    const uint SET_ADVANCED_COLOR_STATE = 10;
    const uint GET_ADVANCED_COLOR_INFO_2 = 15; // Windows 11 24H2 and later
    const uint SET_HDR_STATE = 16;             // Windows 11 24H2 and later

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPath, [Out] PATH_INFO[] paths, ref uint numMode, [Out] MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TARGET_NAME info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref COLOR_INFO info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref COLOR_INFO_2 info);
    [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref SET_STATE info);

    static HEADER MakeHeader(uint type, int size, LUID adapter, uint id) {
        HEADER h = new HEADER(); h.type = type; h.size = (uint)size; h.adapterId = adapter; h.id = id; return h;
    }

    public static List<HdrDisplay> GetDisplays() {
        uint numPath, numMode;
        int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPath, out numMode);
        if (err != 0) throw new Exception("GetDisplayConfigBufferSizes failed: " + err);
        PATH_INFO[] paths = new PATH_INFO[numPath];
        MODE_INFO[] modes = new MODE_INFO[numMode];
        err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPath, paths, ref numMode, modes, IntPtr.Zero);
        if (err != 0) throw new Exception("QueryDisplayConfig failed: " + err);

        List<HdrDisplay> result = new List<HdrDisplay>();
        HashSet<string> seen = new HashSet<string>();
        for (int i = 0; i < numPath; i++) {
            LUID adapter = paths[i].target.adapterId;
            uint id = paths[i].target.id;
            HdrDisplay d = new HdrDisplay();
            d.AdapterLow = adapter.LowPart; d.AdapterHigh = adapter.HighPart; d.TargetId = id;
            if (!seen.Add(d.Key)) continue;

            TARGET_NAME name = new TARGET_NAME();
            name.header = MakeHeader(GET_TARGET_NAME, Marshal.SizeOf(typeof(TARGET_NAME)), adapter, id);
            d.Name = (DisplayConfigGetDeviceInfo(ref name) == 0 && !string.IsNullOrEmpty(name.friendlyName))
                ? name.friendlyName : "Display " + id;

            COLOR_INFO_2 c2 = new COLOR_INFO_2();
            c2.header = MakeHeader(GET_ADVANCED_COLOR_INFO_2, Marshal.SizeOf(typeof(COLOR_INFO_2)), adapter, id);
            if (DisplayConfigGetDeviceInfo(ref c2) == 0) {
                d.UsesNewApi = true;
                d.HdrSupported = (c2.value & (1u << 4)) != 0; // highDynamicRangeSupported
                d.HdrEnabled = c2.activeColorMode == 2;       // DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR
            } else {
                COLOR_INFO c1 = new COLOR_INFO();
                c1.header = MakeHeader(GET_ADVANCED_COLOR_INFO, Marshal.SizeOf(typeof(COLOR_INFO)), adapter, id);
                if (DisplayConfigGetDeviceInfo(ref c1) == 0) {
                    d.HdrSupported = (c1.value & 1u) != 0;
                    d.HdrEnabled = (c1.value & 2u) != 0;
                }
            }
            result.Add(d);
        }
        return result;
    }

    public static int SetHdr(HdrDisplay d, bool enable) {
        LUID adapter = new LUID(); adapter.LowPart = d.AdapterLow; adapter.HighPart = d.AdapterHigh;
        SET_STATE s = new SET_STATE();
        s.header = MakeHeader(d.UsesNewApi ? SET_HDR_STATE : SET_ADVANCED_COLOR_STATE, Marshal.SizeOf(typeof(SET_STATE)), adapter, d.TargetId);
        s.value = enable ? 1u : 0u;
        return DisplayConfigSetDeviceInfo(ref s);
    }
}
'@
}

function Get-HdrDisplays {
    [HdrNative]::GetDisplays()
}

function Set-Hdr {
    param([HdrDisplay]$Display, [bool]$Enable)
    $err = [HdrNative]::SetHdr($Display, $Enable)
    if ($err -ne 0) { throw "Could not switch HDR for '$($Display.Name)' (error $err)." }
}

if ($Action) {
    $displays = Get-HdrDisplays
    if ($Action -ne 'status') {
        foreach ($d in $displays | Where-Object HdrSupported) { Set-Hdr $d ($Action -eq 'on') }
        Start-Sleep -Milliseconds 500
        $displays = Get-HdrDisplays
    }
    $displays | Format-Table Name, HdrSupported, HdrEnabled, UsesNewApi -AutoSize
}
