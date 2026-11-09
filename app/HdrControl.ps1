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
    // Only the fields needed here; for source modes the desktop position sits at offset 28/32
    [StructLayout(LayoutKind.Explicit, Size = 64)] public struct MODE_INFO {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(28)] public int positionX;
        [FieldOffset(32)] public int positionY;
    }
    [StructLayout(LayoutKind.Sequential)] public struct HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential)] public struct COLOR_INFO { public HEADER header; public uint value; public uint colorEncoding; public uint bitsPerColorChannel; }
    [StructLayout(LayoutKind.Sequential)] public struct COLOR_INFO_2 { public HEADER header; public uint value; public uint colorEncoding; public uint bitsPerColorChannel; public uint activeColorMode; }
    [StructLayout(LayoutKind.Sequential)] public struct SET_STATE { public HEADER header; public uint value; }
    [StructLayout(LayoutKind.Sequential)] public struct SDR_WHITE_LEVEL { public HEADER header; public uint level; }
    [StructLayout(LayoutKind.Sequential)] public struct SET_SDR_WHITE_LEVEL { public HEADER header; public uint level; public byte finalValue; }
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
    const uint GET_SDR_WHITE_LEVEL = 11;
    const uint SET_SDR_WHITE_LEVEL_TYPE = 0xFFFFFFEE; // undocumented, used by the Windows display settings
    const uint MODE_INFO_TYPE_SOURCE = 1;
    const uint MODE_IDX_INVALID = 0xFFFFFFFF;
    const uint SDC_TOPOLOGY_EXTEND = 0x4, SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_APPLY = 0x80, SDC_ALLOW_CHANGES = 0x400;

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);
    [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPath, [In] PATH_INFO[] paths, uint numMode, [In] MODE_INFO[] modes, uint flags);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SDR_WHITE_LEVEL info);
    [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref SET_SDR_WHITE_LEVEL info);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPath, [Out] PATH_INFO[] paths, ref uint numMode, [Out] MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TARGET_NAME info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref COLOR_INFO info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref COLOR_INFO_2 info);
    [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref SET_STATE info);

    static HEADER MakeHeader(uint type, int size, LUID adapter, uint id) {
        HEADER h = new HEADER(); h.type = type; h.size = (uint)size; h.adapterId = adapter; h.id = id; return h;
    }

    static void QueryActive(out PATH_INFO[] paths, out MODE_INFO[] modes) {
        uint numPath, numMode;
        int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPath, out numMode);
        if (err != 0) throw new Exception("GetDisplayConfigBufferSizes failed: " + err);
        paths = new PATH_INFO[numPath];
        modes = new MODE_INFO[numMode];
        err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPath, paths, ref numMode, modes, IntPtr.Zero);
        if (err != 0) throw new Exception("QueryDisplayConfig failed: " + err);
        Array.Resize(ref paths, (int)numPath);
        Array.Resize(ref modes, (int)numMode);
    }

    static LUID ToLuid(HdrDisplay d) { LUID l = new LUID(); l.LowPart = d.AdapterLow; l.HighPart = d.AdapterHigh; return l; }

    // SDR content brightness while HDR is on: 1000 = 80 nits; the Windows slider 0..100 % maps to 80..480 nits
    public static int GetSdrWhiteLevel(HdrDisplay d) {
        SDR_WHITE_LEVEL s = new SDR_WHITE_LEVEL();
        s.header = MakeHeader(GET_SDR_WHITE_LEVEL, Marshal.SizeOf(typeof(SDR_WHITE_LEVEL)), ToLuid(d), d.TargetId);
        return DisplayConfigGetDeviceInfo(ref s) == 0 ? (int)s.level : -1;
    }

    public static int SetSdrWhiteLevel(HdrDisplay d, uint level) {
        SET_SDR_WHITE_LEVEL s = new SET_SDR_WHITE_LEVEL();
        s.header = MakeHeader(SET_SDR_WHITE_LEVEL_TYPE, Marshal.SizeOf(typeof(SET_SDR_WHITE_LEVEL)), ToLuid(d), d.TargetId);
        s.level = level; s.finalValue = 1;
        return DisplayConfigSetDeviceInfo(ref s);
    }

    public static int CountActiveDisplays() {
        PATH_INFO[] paths; MODE_INFO[] modes;
        QueryActive(out paths, out modes);
        return paths.Length;
    }

    // Keeps only the display(s) showing the primary desktop (position 0,0) and turns all others off
    public static int DetachSecondary() {
        PATH_INFO[] paths; MODE_INFO[] modes;
        QueryActive(out paths, out modes);
        List<PATH_INFO> keep = new List<PATH_INFO>();
        List<MODE_INFO> keepModes = new List<MODE_INFO>();
        Dictionary<uint, uint> remap = new Dictionary<uint, uint>();
        foreach (PATH_INFO p in paths) {
            uint si = p.source.modeInfoIdx;
            bool primary = si < modes.Length && modes[si].infoType == MODE_INFO_TYPE_SOURCE &&
                           modes[si].positionX == 0 && modes[si].positionY == 0;
            if (!primary) continue;
            PATH_INFO np = p;
            np.source.modeInfoIdx = Remap(si, modes, keepModes, remap);
            np.target.modeInfoIdx = Remap(p.target.modeInfoIdx, modes, keepModes, remap);
            keep.Add(np);
        }
        if (keep.Count == 0 || keep.Count == paths.Length) return -1;   // nothing to turn off
        return SetDisplayConfig((uint)keep.Count, keep.ToArray(), (uint)keepModes.Count, keepModes.ToArray(),
                                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES);
    }

    static uint Remap(uint idx, MODE_INFO[] modes, List<MODE_INFO> keepModes, Dictionary<uint, uint> remap) {
        if (idx == MODE_IDX_INVALID || idx >= modes.Length) return MODE_IDX_INVALID;
        uint n;
        if (!remap.TryGetValue(idx, out n)) { n = (uint)keepModes.Count; keepModes.Add(modes[idx]); remap[idx] = n; }
        return n;
    }

    // Brings back the last extended layout Windows remembers for the connected monitors
    public static int RestoreExtended() {
        return SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
    }

    public static List<HdrDisplay> GetDisplays() {
        PATH_INFO[] paths; MODE_INFO[] modes;
        QueryActive(out paths, out modes);
        uint numPath = (uint)paths.Length;

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

# SDR brightness in HDR mode as the percentage of the Windows slider (0..100), or -1 if unknown
function Get-SdrBrightness([HdrDisplay]$Display) {
    $level = [HdrNative]::GetSdrWhiteLevel($Display)
    if ($level -lt 0) { return -1 }
    [math]::Max(0, [math]::Min(100, [math]::Round(($level - 1000) / 50)))
}

function Set-SdrBrightness([HdrDisplay]$Display, [int]$Percent) {
    $level = [uint32](1000 + 50 * [math]::Max(0, [math]::Min(100, $Percent)))
    $err = [HdrNative]::SetSdrWhiteLevel($Display, $level)
    if ($err -ne 0) { throw "Could not set SDR brightness for '$($Display.Name)' (error $err)." }
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
