using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GameSwitcher.Native
{
    class MonitorLevel
    {
        public string Key, Name;   // Key: Windows display name and monitor index, e.g. \\.\DISPLAY1#0
        public int Percent;
    }

    // Brightness of external monitors via DDC/CI (dxva2) and of built-in screens via WMI; others are skipped.
    static class MonitorBrightness
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR { public IntPtr Handle; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX { public int cbSize; public int l, t, r, b, wl, wt, wr, wb; public int Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
        delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
        [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
        [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll")] static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);
        [DllImport("dxva2.dll")] static extern bool SetMonitorBrightness(IntPtr monitor, uint value);

        // Calls body(key, name, handle, min, current, max) for every monitor that answers via DDC/CI
        static void ForEach(Action<string, string, IntPtr, uint, uint, uint> body)
        {
            var monitors = new List<IntPtr>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, dc, r, d) => { monitors.Add(m); return true; }, IntPtr.Zero);
            foreach (IntPtr m in monitors)
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                if (!GetMonitorInfo(m, ref info)) continue;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(m, out uint count) || count == 0) continue;
                var physical = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(m, count, physical)) continue;
                try
                {
                    for (int i = 0; i < count; i++)
                        if (GetMonitorBrightness(physical[i].Handle, out uint min, out uint cur, out uint max) && max > min)
                            body(info.Device + "#" + i, physical[i].Description, physical[i].Handle, min, cur, max);
                }
                finally { DestroyPhysicalMonitors(count, physical); }
            }
        }

        // External monitors via DDC/CI and built-in screens (laptops, tablets) via WMI
        public static List<MonitorLevel> Get()
        {
            var result = new List<MonitorLevel>();
            ForEach((key, name, h, min, cur, max) =>
                result.Add(new MonitorLevel { Key = key, Name = name, Percent = (int)Math.Round((cur - min) * 100.0 / (max - min)) }));
            try
            {
                foreach (var m in Wmi("WmiMonitorBrightness"))
                    using (m)
                        result.Add(new MonitorLevel { Key = "wmi:" + m["InstanceName"], Name = Strings.T("monitor.internal"), Percent = Convert.ToInt32(m["CurrentBrightness"]) });
            }
            catch { }   // no built-in screen
            return result;
        }

        static System.Collections.Generic.IEnumerable<System.Management.ManagementObject> Wmi(string cls)
        {
            using (var searcher = new System.Management.ManagementObjectSearcher(@"root\wmi", "SELECT * FROM " + cls + " WHERE Active = TRUE"))
                foreach (System.Management.ManagementObject m in searcher.Get()) yield return m;
        }

        // Sets one monitor (key) or all (null) to a percentage of their range
        public static int Set(string key, int percent)
        {
            int changed = 0;
            percent = Math.Max(0, Math.Min(100, percent));
            ForEach((k, name, h, min, cur, max) =>
            {
                if (key != null && k != key) return;
                if (SetMonitorBrightness(h, (uint)Math.Round(min + (max - min) * percent / 100.0))) changed++;
            });
            if (key == null || key.StartsWith("wmi:"))
            {
                try
                {
                    foreach (var m in Wmi("WmiMonitorBrightnessMethods"))
                        using (m)
                        {
                            if (key != null && "wmi:" + m["InstanceName"] != key) continue;
                            m.InvokeMethod("WmiSetBrightness", new object[] { (uint)0, (byte)percent });
                            changed++;
                        }
                }
                catch { }
            }
            return changed;
        }
    }
}
