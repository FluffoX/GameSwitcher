using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GameSwitcher.Native
{
    class HdrDisplay
    {
        public string Name;
        public uint AdapterLow;
        public int AdapterHigh;
        public uint TargetId;
        public bool HdrSupported, HdrEnabled, UsesNewApi;
        public string Key => AdapterHigh + ":" + AdapterLow + ":" + TargetId;
    }

    // HDR, SDR brightness and monitor layout via the Windows DisplayConfig API
    static class DisplayConfig
    {
        [StructLayout(LayoutKind.Sequential)] struct LUID { public uint LowPart; public int HighPart; }
        [StructLayout(LayoutKind.Sequential)] struct PATH_SOURCE { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
        [StructLayout(LayoutKind.Sequential)] struct PATH_TARGET
        {
            public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
            public uint rotation; public uint scaling; public uint refreshNum; public uint refreshDen;
            public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
        }
        [StructLayout(LayoutKind.Sequential)] struct PATH_INFO { public PATH_SOURCE source; public PATH_TARGET target; public uint flags; }
        // Only the fields needed here; for source modes the desktop position sits at offset 28/32
        [StructLayout(LayoutKind.Explicit, Size = 64)] struct MODE_INFO
        {
            [FieldOffset(0)] public uint infoType;
            [FieldOffset(28)] public int positionX;
            [FieldOffset(32)] public int positionY;
        }
        [StructLayout(LayoutKind.Sequential)] struct HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }
        [StructLayout(LayoutKind.Sequential)] struct COLOR_INFO { public HEADER header; public uint value; public uint colorEncoding; public uint bitsPerColorChannel; }
        [StructLayout(LayoutKind.Sequential)] struct COLOR_INFO_2 { public HEADER header; public uint value; public uint colorEncoding; public uint bitsPerColorChannel; public uint activeColorMode; }
        [StructLayout(LayoutKind.Sequential)] struct SET_STATE { public HEADER header; public uint value; }
        [StructLayout(LayoutKind.Sequential)] struct SDR_WHITE_LEVEL { public HEADER header; public uint level; }
        [StructLayout(LayoutKind.Sequential)] struct SET_SDR_WHITE_LEVEL { public HEADER header; public uint level; public byte finalValue; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct TARGET_NAME
        {
            public HEADER header; public uint flags; public uint outputTechnology;
            public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string friendlyName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string devicePath;
        }

        const uint QDC_ONLY_ACTIVE_PATHS = 2;
        const uint GET_TARGET_NAME = 2;
        const uint GET_ADVANCED_COLOR_INFO = 9;
        const uint SET_ADVANCED_COLOR_STATE = 10;
        const uint GET_SDR_WHITE_LEVEL = 11;
        const uint GET_ADVANCED_COLOR_INFO_2 = 15;          // Windows 11 24H2 and later
        const uint SET_HDR_STATE = 16;                      // Windows 11 24H2 and later
        const uint SET_SDR_WHITE_LEVEL_TYPE = 0xFFFFFFEE;   // undocumented, used by the Windows display settings
        const uint MODE_INFO_TYPE_SOURCE = 1;
        const uint MODE_IDX_INVALID = 0xFFFFFFFF;
        const uint SDC_TOPOLOGY_EXTEND = 0x4, SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_APPLY = 0x80, SDC_ALLOW_CHANGES = 0x400;

        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPath, [Out] PATH_INFO[] paths, ref uint numMode, [Out] MODE_INFO[] modes, IntPtr topology);
        [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPath, [In] PATH_INFO[] paths, uint numMode, [In] MODE_INFO[] modes, uint flags);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TARGET_NAME info);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref COLOR_INFO info);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref COLOR_INFO_2 info);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SDR_WHITE_LEVEL info);
        [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref SET_STATE info);
        [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref SET_SDR_WHITE_LEVEL info);

        static HEADER MakeHeader(uint type, int size, LUID adapter, uint id) =>
            new HEADER { type = type, size = (uint)size, adapterId = adapter, id = id };

        static LUID ToLuid(HdrDisplay d) => new LUID { LowPart = d.AdapterLow, HighPart = d.AdapterHigh };

        static void QueryActive(out PATH_INFO[] paths, out MODE_INFO[] modes)
        {
            int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint numPath, out uint numMode);
            if (err != 0) throw new Exception("GetDisplayConfigBufferSizes failed: " + err);
            paths = new PATH_INFO[numPath];
            modes = new MODE_INFO[numMode];
            err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPath, paths, ref numMode, modes, IntPtr.Zero);
            if (err != 0) throw new Exception("QueryDisplayConfig failed: " + err);
            Array.Resize(ref paths, (int)numPath);
            Array.Resize(ref modes, (int)numMode);
        }

        public static List<HdrDisplay> GetDisplays()
        {
            QueryActive(out PATH_INFO[] paths, out _);
            var result = new List<HdrDisplay>();
            var seen = new HashSet<string>();
            foreach (PATH_INFO p in paths)
            {
                LUID adapter = p.target.adapterId;
                uint id = p.target.id;
                var d = new HdrDisplay { AdapterLow = adapter.LowPart, AdapterHigh = adapter.HighPart, TargetId = id };
                if (!seen.Add(d.Key)) continue;

                var name = new TARGET_NAME { header = MakeHeader(GET_TARGET_NAME, Marshal.SizeOf(typeof(TARGET_NAME)), adapter, id) };
                d.Name = DisplayConfigGetDeviceInfo(ref name) == 0 && !string.IsNullOrEmpty(name.friendlyName) ? name.friendlyName : "Display " + id;

                var c2 = new COLOR_INFO_2 { header = MakeHeader(GET_ADVANCED_COLOR_INFO_2, Marshal.SizeOf(typeof(COLOR_INFO_2)), adapter, id) };
                if (DisplayConfigGetDeviceInfo(ref c2) == 0)
                {
                    d.UsesNewApi = true;
                    d.HdrSupported = (c2.value & (1u << 4)) != 0;   // highDynamicRangeSupported
                    d.HdrEnabled = c2.activeColorMode == 2;         // DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR
                }
                else
                {
                    var c1 = new COLOR_INFO { header = MakeHeader(GET_ADVANCED_COLOR_INFO, Marshal.SizeOf(typeof(COLOR_INFO)), adapter, id) };
                    if (DisplayConfigGetDeviceInfo(ref c1) == 0)
                    {
                        d.HdrSupported = (c1.value & 1u) != 0;
                        d.HdrEnabled = (c1.value & 2u) != 0;
                    }
                }
                result.Add(d);
            }
            return result;
        }

        public static void SetHdr(HdrDisplay d, bool enable)
        {
            var s = new SET_STATE
            {
                header = MakeHeader(d.UsesNewApi ? SET_HDR_STATE : SET_ADVANCED_COLOR_STATE, Marshal.SizeOf(typeof(SET_STATE)), ToLuid(d), d.TargetId),
                value = enable ? 1u : 0u
            };
            int err = DisplayConfigSetDeviceInfo(ref s);
            if (err != 0) throw new Exception($"Could not switch HDR for '{d.Name}' (error {err}).");
        }

        // SDR content brightness while HDR is on, as the Windows slider percentage (0..100), or -1 if unknown.
        // Level 1000 = 80 nits; the slider 0..100 % maps to 80..480 nits.
        public static int GetSdrBrightness(HdrDisplay d)
        {
            var s = new SDR_WHITE_LEVEL { header = MakeHeader(GET_SDR_WHITE_LEVEL, Marshal.SizeOf(typeof(SDR_WHITE_LEVEL)), ToLuid(d), d.TargetId) };
            if (DisplayConfigGetDeviceInfo(ref s) != 0) return -1;
            return Math.Max(0, Math.Min(100, (int)Math.Round(((int)s.level - 1000) / 50.0)));
        }

        public static void SetSdrBrightness(HdrDisplay d, int percent)
        {
            var s = new SET_SDR_WHITE_LEVEL
            {
                header = MakeHeader(SET_SDR_WHITE_LEVEL_TYPE, Marshal.SizeOf(typeof(SET_SDR_WHITE_LEVEL)), ToLuid(d), d.TargetId),
                level = (uint)(1000 + 50 * Math.Max(0, Math.Min(100, percent))),
                finalValue = 1
            };
            int err = DisplayConfigSetDeviceInfo(ref s);
            if (err != 0) throw new Exception($"Could not set SDR brightness for '{d.Name}' (error {err}).");
        }

        public static int CountActiveDisplays()
        {
            QueryActive(out PATH_INFO[] paths, out _);
            return paths.Length;
        }

        // Keeps only the display(s) showing the primary desktop (position 0,0) and turns all others off.
        // Returns false if there was nothing to turn off.
        public static bool DetachSecondary()
        {
            QueryActive(out PATH_INFO[] paths, out MODE_INFO[] modes);
            var keep = new List<PATH_INFO>();
            var keepModes = new List<MODE_INFO>();
            var remap = new Dictionary<uint, uint>();
            uint Remap(uint idx)
            {
                if (idx == MODE_IDX_INVALID || idx >= modes.Length) return MODE_IDX_INVALID;
                if (!remap.TryGetValue(idx, out uint n)) { n = (uint)keepModes.Count; keepModes.Add(modes[idx]); remap[idx] = n; }
                return n;
            }
            foreach (PATH_INFO p in paths)
            {
                uint si = p.source.modeInfoIdx;
                bool primary = si < modes.Length && modes[si].infoType == MODE_INFO_TYPE_SOURCE &&
                               modes[si].positionX == 0 && modes[si].positionY == 0;
                if (!primary) continue;
                PATH_INFO np = p;
                np.source.modeInfoIdx = Remap(si);
                np.target.modeInfoIdx = Remap(p.target.modeInfoIdx);
                keep.Add(np);
            }
            if (keep.Count == 0 || keep.Count == paths.Length) return false;
            int err = SetDisplayConfig((uint)keep.Count, keep.ToArray(), (uint)keepModes.Count, keepModes.ToArray(),
                                       SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES);
            if (err != 0) throw new Exception("SetDisplayConfig error " + err);
            return true;
        }

        // Brings back the last extended layout Windows remembers for the connected monitors
        public static void RestoreExtended()
        {
            int err = SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
            if (err != 0) throw new Exception("SetDisplayConfig error " + err);
        }
    }
}
