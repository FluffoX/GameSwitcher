using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameSwitcher.Native
{
    class DisplayMode
    {
        public int Width, Height, Hz;
        public override string ToString() => $"{Width} × {Height} @ {Hz} Hz";
    }

    // Resolution / refresh rate of the primary display
    static class DisplayModes
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        const int ENUM_CURRENT_SETTINGS = -1;
        const int DISPLAY_DEVICE_PRIMARY_DEVICE = 4;
        const int DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);

        static string PrimaryDevice()
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
            for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
            {
                if ((dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0) return dd.DeviceName;
                dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            }
            return null;
        }

        static DEVMODE NewDevMode() => new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };

        public static DisplayMode Current()
        {
            DEVMODE dm = NewDevMode();
            if (!EnumDisplaySettings(PrimaryDevice(), ENUM_CURRENT_SETTINGS, ref dm)) return null;
            return new DisplayMode { Width = dm.dmPelsWidth, Height = dm.dmPelsHeight, Hz = dm.dmDisplayFrequency };
        }

        // All distinct resolution/refresh combinations of the primary display, largest first
        public static List<DisplayMode> All()
        {
            string device = PrimaryDevice();
            var result = new List<DisplayMode>();
            var seen = new HashSet<string>();
            DEVMODE dm = NewDevMode();
            for (int i = 0; EnumDisplaySettings(device, i, ref dm); i++)
            {
                if (dm.dmBitsPerPel < 32 || !seen.Add(dm.dmPelsWidth + "x" + dm.dmPelsHeight + "@" + dm.dmDisplayFrequency)) continue;
                result.Add(new DisplayMode { Width = dm.dmPelsWidth, Height = dm.dmPelsHeight, Hz = dm.dmDisplayFrequency });
            }
            return result.OrderByDescending(m => m.Width).ThenByDescending(m => m.Height).ThenByDescending(m => m.Hz).ToList();
        }

        // Changes the mode dynamically (not saved permanently, so a restart always brings back the normal mode)
        public static void Set(int width, int height, int hz)
        {
            string device = PrimaryDevice();
            DEVMODE dm = NewDevMode();
            if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm)) throw new Exception("Display mode unavailable.");
            dm.dmPelsWidth = width; dm.dmPelsHeight = height; dm.dmDisplayFrequency = hz;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            int r = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
            if (r != 0) throw new Exception("ChangeDisplaySettingsEx error " + r);
        }
    }

    // Keeps the display on; must be called from the thread that stays alive while the game runs
    static class KeepAwake
    {
        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 1, ES_DISPLAY_REQUIRED = 2;
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

        public static void Set(bool on) =>
            SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);
    }

    class PowerPlan
    {
        public string Id, Name;
        public bool Active;
    }

    // Power plans via powercfg
    static class PowerPlans
    {
        static string Run(string args)
        {
            var psi = new ProcessStartInfo("powercfg.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
            };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) throw new Exception($"powercfg {args} failed ({p.ExitCode}).");
                return output;
            }
        }

        public static List<PowerPlan> List() =>
            Regex.Matches(Run("/list"), @"([0-9a-fA-F]{8}-[0-9a-fA-F-]{27})\s+\((.+?)\)(\s*\*)?").Cast<Match>()
                 .Select(m => new PowerPlan { Id = m.Groups[1].Value, Name = m.Groups[2].Value, Active = m.Groups[3].Success })
                 .ToList();

        public static PowerPlan Active() => List().FirstOrDefault(p => p.Active);

        public static void Activate(string id) => Run("/setactive " + id);
    }

    // Windows stores the night light state in an undocumented blob. Only the known format is changed:
    // 0x13 at byte 18 = off, 0x15 = on, and "on" has two extra bytes (0x10 0x00) at position 23.
    static class NightLight
    {
        const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

        static byte[] Read()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath)) return key?.GetValue("Data") as byte[];
        }

        // true = on, false = off, null = format unknown (not supported on this PC)
        public static bool? Get()
        {
            byte[] d = Read();
            if (d == null) return null;
            if (d.Length == 43 && d[18] == 0x15 && d[23] == 0x10 && d[24] == 0x00) return true;
            if (d.Length == 41 && d[18] == 0x13) return false;
            return null;
        }

        public static void Set(bool enable)
        {
            bool? state = Get();
            if (state == null) throw new Exception("Night light format not supported on this PC.");
            if (state == enable) return;
            var d = Read().ToList();
            if (enable) { d.InsertRange(23, new byte[] { 0x10, 0x00 }); d[18] = 0x15; }
            else { d.RemoveRange(23, 2); d[18] = 0x13; }
            // bump the change counter so Windows picks up the new state
            for (int i = 10; i < 15; i++) { if (d[i] != 0xff) { d[i]++; break; } }
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, true))
                key.SetValue("Data", d.ToArray(), RegistryValueKind.Binary);
        }
    }
}
