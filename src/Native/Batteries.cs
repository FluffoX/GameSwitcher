using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace GameSwitcher.Native
{
    // Battery level of a device. Some sources only report coarse levels (Level instead of Percent).
    class Battery
    {
        public string Name { get; set; }
        public string Kind { get; set; } = "device";   // controller, mouse, keyboard, headset, device
        public int Percent { get; set; } = -1;
        public string Level { get; set; } = "";         // empty, low, medium, full (when Percent is unknown)
        public bool Charging { get; set; }

        // A percentage for comparisons with the warning threshold (-1: unknown)
        public int Estimate() => Percent >= 0 ? Percent
            : Level == "empty" ? 5 : Level == "low" ? 15 : Level == "medium" ? 50 : Level == "full" ? 100 : -1;

        // "DualSense: 80 % (charging)"
        public string Text(string separator = ": ") => Name + separator + (Percent >= 0 ? Percent + " %" : Strings.T("battery." + (Level.Length > 0 ? Level : "medium")))
            + (Charging ? " (" + Strings.T("battery.charging") + ")" : "");
    }

    // battery.json, written by the service for the window
    class BatteryFile
    {
        public string Time { get; set; }
        public List<Battery> Devices { get; set; } = new List<Battery>();
    }

    // Reads battery levels of gaming devices of any brand; every source is best effort and never throws:
    // XInput controllers (Xbox and compatible), PlayStation and Nintendo controllers (HID input reports),
    // Logitech wireless devices (HID++ 2.0), any HID device with the standard battery value
    // and Bluetooth devices that report a level to Windows.
    static class Batteries
    {
        public static List<Battery> Read()
        {
            var result = new List<Battery>();
            void Add(Func<IEnumerable<Battery>> source)
            {
                try { result.AddRange(source()); } catch { }
            }
            List<HidInterface> hid = null;
            try { hid = Hid.Enumerate(); } catch { hid = new List<HidInterface>(); }
            Add(XInput);
            Add(() => PlayStation(hid));
            Add(() => Nintendo(hid));
            Add(() => Logitech(hid));
            Add(() => StandardHid(hid));
            Add(Bluetooth);
            // the same device can appear through two sources: keep the more precise entry
            return result.GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.OrderByDescending(b => b.Percent).First()).ToList();
        }

        // Connected game controllers (couch mode), as stable IDs: XInput slots and HID gamepads/joysticks
        // of any brand. Keyboards, mice and KVM adapters sometimes add a gamepad collection: a controller
        // only counts if its device has no keyboard or mouse collection and it sends full reports.
        public static HashSet<string> ConnectedControllers()
        {
            var ids = new HashSet<string>();
            try { foreach (int slot in XInputSlots()) ids.Add("xinput:" + slot); } catch { }
            try
            {
                var hid = Hid.Enumerate();
                var inputDevices = new HashSet<string>(hid.Where(d => d.UsagePage == 1 && (d.Usage == 2 || d.Usage == 6)).Select(DeviceOf));
                foreach (var d in hid.Where(d => d.UsagePage == 1 && (d.Usage == 4 || d.Usage == 5) && d.InputLength >= 6 && !inputDevices.Contains(DeviceOf(d))))
                    ids.Add(DeviceOf(d));
            }
            catch { }
            return ids;
        }

        // The physical interface of a HID collection: its path without the collection number,
        // e.g. \\?\hid#vid_413d&pid_2107&mi_00&col03#9&10cd9d74&0&0002#{...} -> vid_413d&pid_2107&mi_00#9&10cd9d74&0
        public static string DeviceOf(HidInterface d)
        {
            var m = System.Text.RegularExpressions.Regex.Match(d.Path.ToLowerInvariant(), @"hid#([^#]*?)(&col[0-9a-f]+)?#([^#]*?)(&[0-9a-f]{4})?#");
            return m.Success ? m.Groups[1].Value + "#" + m.Groups[3].Value : d.Path.ToLowerInvariant();
        }

        #region Xbox (XInput)

        [StructLayout(LayoutKind.Sequential)] struct XINPUT_STATE { public uint PacketNumber; public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LX, LY, RX, RY; }
        [StructLayout(LayoutKind.Sequential)] struct XINPUT_BATTERY_INFORMATION { public byte BatteryType, BatteryLevel; }
        [DllImport("xinput1_4.dll")] static extern int XInputGetState(int user, out XINPUT_STATE state);
        [DllImport("xinput1_4.dll")] static extern int XInputGetBatteryInformation(int user, byte deviceType, out XINPUT_BATTERY_INFORMATION info);

        static IEnumerable<int> XInputSlots() => Enumerable.Range(0, 4).Where(i => XInputGetState(i, out _) == 0).ToList();

        static IEnumerable<Battery> XInput()
        {
            var slots = XInputSlots().ToList();
            foreach (int i in slots)
            {
                if (XInputGetBatteryInformation(i, 0, out var info) != 0) continue;
                if (info.BatteryType != 2 && info.BatteryType != 3) continue;   // alkaline or NiMH; wired and unknown have no level
                string level = info.BatteryLevel == 0 ? "empty" : info.BatteryLevel == 1 ? "low" : info.BatteryLevel == 2 ? "medium" : "full";
                // XInput does not tell the model (Xbox, 8BitDo, ...): the slot number tells controllers apart
                yield return new Battery { Name = "Controller " + (i + 1), Kind = "controller", Level = level };
            }
        }

        #endregion

        #region PlayStation (DualSense, DualShock 4)

        static readonly int[] DualSense = { 0x0CE6, 0x0DF2 }, DualShock4 = { 0x05C4, 0x09CC, 0x0BA0 };

        static IEnumerable<Battery> PlayStation(List<HidInterface> hid)
        {
            foreach (var d in hid.Where(d => d.Vid == 0x054C && d.UsagePage == 1 && d.Usage == 5))
            {
                bool sense = DualSense.Contains(d.Pid);
                if (!sense && !DualShock4.Contains(d.Pid)) continue;
                Battery b = null;
                using (var h = Hid.Handle.Open(d))
                {
                    if (h == null) continue;
                    // the controller sends reports continuously; a few tries find one with the battery field
                    for (int tries = 0; tries < 4 && b == null; tries++)
                    {
                        byte[] r = h.Read(250);
                        if (r == null) break;
                        b = sense ? ParseDualSense(r) : ParseDualShock4(r);
                    }
                }
                if (b != null)
                {
                    b.Name = sense ? (d.Pid == 0x0DF2 ? "DualSense Edge" : "DualSense") : "DualShock 4";
                    yield return b;
                }
            }
        }

        // USB report 0x01 (status byte 53), Bluetooth report 0x31 (status byte 54);
        // low nibble: level 0..10, high nibble: 0 discharging, 1 charging, 2 full
        static Battery ParseDualSense(byte[] r)
        {
            int at = r[0] == 0x01 && r.Length >= 64 ? 53 : r[0] == 0x31 && r.Length >= 78 ? 54 : -1;
            if (at < 0) return null;
            int level = r[at] & 0x0F, state = r[at] >> 4;
            if (state > 2) return null;   // error / unknown
            return new Battery { Kind = "controller", Percent = state == 2 ? 100 : Math.Min(level * 10 + 5, 100), Charging = state == 1 };
        }

        // USB report 0x01 (status byte 30), Bluetooth report 0x11 (status byte 32);
        // low nibble: level, bit 4: cable connected (then 0..10, 11 = full; otherwise 0..9)
        static Battery ParseDualShock4(byte[] r)
        {
            int at = r[0] == 0x01 && r.Length >= 64 ? 30 : r[0] == 0x11 && r.Length >= 78 ? 32 : -1;
            if (at < 0) return null;
            int level = r[at] & 0x0F;
            bool cable = (r[at] & 0x10) != 0;
            if (cable && level >= 11) return new Battery { Kind = "controller", Percent = 100 };
            return new Battery { Kind = "controller", Percent = Math.Min(level * 10 + 5, 100), Charging = cable };
        }

        #endregion

        #region Nintendo (Switch Pro Controller, Joy-Con) and standard HID battery

        static readonly Dictionary<int, string> NintendoNames = new Dictionary<int, string>
        {
            [0x2009] = "Pro Controller", [0x2006] = "Joy-Con (L)", [0x2007] = "Joy-Con (R)", [0x200E] = "Joy-Con"
        };

        static IEnumerable<Battery> Nintendo(List<HidInterface> hid)
        {
            foreach (var d in hid.Where(d => d.Vid == 0x057E && NintendoNames.ContainsKey(d.Pid)))
            {
                Battery b = null;
                using (var h = Hid.Handle.Open(d))
                {
                    if (h == null) continue;
                    for (int tries = 0; tries < 4 && b == null; tries++)
                    {
                        byte[] r = h.Read(250);
                        if (r == null) break;
                        b = ParseNintendo(r);
                    }
                }
                if (b != null) { b.Name = NintendoNames[d.Pid]; yield return b; }
            }
        }

        // Full input reports (0x21, 0x30, 0x31; sent once a game or Steam has switched the controller to them):
        // byte 2, high nibble: 8 full, 6 medium, 4 low, 2 critical, 0 empty; +1 while charging
        static Battery ParseNintendo(byte[] r)
        {
            if (r.Length < 13 || (r[0] != 0x21 && r[0] != 0x30 && r[0] != 0x31)) return null;
            int nibble = r[2] >> 4, level = nibble & 0x0E;
            return new Battery
            {
                Kind = "controller", Charging = (nibble & 1) != 0,
                Level = level >= 8 ? "full" : level >= 6 ? "medium" : level >= 2 ? "low" : "empty"
            };
        }

        // Any other HID device that reports the standard "Battery Strength" value (many wireless
        // gamepads, keyboards, mice and headsets); named after the product string of the device
        static IEnumerable<Battery> StandardHid(List<HidInterface> hid)
        {
            var handled = new HashSet<int> { 0x054C, 0x057E };   // read above in their own format
            var done = new HashSet<string>();
            foreach (var d in hid.Where(d => d.BatteryReportType >= 0 && !handled.Contains(d.Vid)))
            {
                string device = DeviceOf(d);
                if (done.Contains(device)) continue;
                int percent = Hid.ReadBatteryStrength(d);
                if (percent < 0) continue;
                done.Add(device);
                var collections = hid.Where(x => DeviceOf(x) == device && x.UsagePage == 1).Select(x => x.Usage).ToList();
                string kind = collections.Contains(4) || collections.Contains(5) ? "controller" : collections.Contains(2) ? "mouse" : collections.Contains(6) ? "keyboard" : GuessKind(d.Product ?? "");
                string name = !string.IsNullOrEmpty(d.Product) ? d.Product : $"HID {d.Vid:X4}:{d.Pid:X4}";
                yield return new Battery { Name = name, Kind = kind, Percent = percent };
            }
        }

        #endregion

        #region Logitech (HID++ 2.0)


        // Wireless Logitech devices sleep when idle and only answer after being woken up, so they are
        // queried less often than the other sources (the last result is reused in between)
        static readonly TimeSpan LogitechInterval = TimeSpan.FromMinutes(5);
        static List<Battery> logitechCache = new List<Battery>();
        static DateTime logitechTime = DateTime.MinValue, logitechAnswered = DateTime.MinValue;

        // Every request gets its own software ID (1..15), so late answers to earlier requests and
        // answers to other programs (e.g. Logitech G HUB) are never mistaken for the current one
        static int softwareId;
        static byte NextSoftwareId() => (byte)(softwareId = softwareId % 15 + 1);

        static bool Send(Hid.Handle h, byte device, byte feature, int function, byte sw, params byte[] args)
        {
            var req = new byte[20];
            req[0] = 0x11; req[1] = device; req[2] = feature; req[3] = (byte)((function << 4) | sw);
            Array.Copy(args, 0, req, 4, Math.Min(args.Length, 16));
            return h.Write(req, 200);
        }

        // Waits for the answer to one request; an error answer counts as "no"
        static byte[] Wait(Hid.Handle h, byte device, byte feature, int function, byte sw, int timeoutMs, out bool error)
        {
            error = false;
            byte funcSw = (byte)((function << 4) | sw);
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                byte[] r = h.Read(Math.Max(1, (int)(until - DateTime.UtcNow).TotalMilliseconds));
                if (r == null || r.Length < 7 || r[0] != 0x11 || r[1] != device) continue;
                if (r[2] == 0xFF && r[3] == feature && r[4] == funcSw) { error = true; return null; }
                if (r[2] == feature && r[3] == funcSw) return r;
            }
            return null;
        }

        // A request with one retry (the first one may only wake the device up)
        static byte[] Request(Hid.Handle h, byte device, byte feature, int function, params byte[] args)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                byte sw = NextSoftwareId();
                if (!Send(h, device, feature, function, sw, args)) return null;
                byte[] r = Wait(h, device, feature, function, sw, 500, out bool error);
                if (r != null || error) return r;
            }
            return null;
        }

        static byte FeatureIndex(Hid.Handle h, byte device, int feature)
        {
            byte[] r = Request(h, device, 0x00, 0, (byte)(feature >> 8), (byte)feature);
            return r == null ? (byte)0 : r[4];
        }

        // Which device indexes answer: 0xFF is a device connected directly (cable or own Bluetooth),
        // 1..6 are devices paired with a receiver. All are pinged at once, up to three times (to wake sleeping devices).
        static List<byte> FindDevices(Hid.Handle h)
        {
            var candidates = new List<byte> { 0xFF, 1, 2, 3, 4, 5, 6 };
            var found = new List<byte>();
            for (int round = 0; round < 3 && found.Count == 0; round++)
            {
                byte sw = NextSoftwareId();
                foreach (byte d in candidates) Send(h, d, 0x00, 1, sw, 0, 0, 0x5A);   // root feature, ping
                DateTime until = DateTime.UtcNow.AddMilliseconds(1000);
                while (DateTime.UtcNow < until)
                {
                    byte[] r = h.Read(Math.Max(1, (int)(until - DateTime.UtcNow).TotalMilliseconds));
                    if (r != null && r.Length >= 7 && r[0] == 0x11 && r[2] == 0x00 && r[3] == ((1 << 4) | sw) && candidates.Contains(r[1]) && !found.Contains(r[1]))
                        found.Add(r[1]);
                }
            }
            return found.Contains(0xFF) ? new List<byte> { 0xFF } : found;
        }

        static Battery LogitechDevice(Hid.Handle h, byte device)
        {
            var b = new Battery();
            // devices report their battery through one of four features, newest first
            byte unified = FeatureIndex(h, device, 0x1004), status = 0, voltage = 0, adc = 0;
            if (unified == 0) status = FeatureIndex(h, device, 0x1000);
            if (unified == 0 && status == 0) voltage = FeatureIndex(h, device, 0x1001);
            if (unified == 0 && status == 0 && voltage == 0) adc = FeatureIndex(h, device, 0x1F20);
            if (voltage != 0 || adc != 0)
            {
                // battery voltage (many Lightspeed mice) or ADC measurement (headsets): millivolts plus flags
                byte[] r = Request(h, device, voltage != 0 ? voltage : adc, 0);
                if (r == null) return null;
                int mv = (r[4] << 8) | r[5], flags = r[6];
                if (adc != 0 && (flags & 1) == 0) return null;   // headset switched off
                b.Percent = PercentFromVoltage(mv);
                b.Charging = voltage != 0 ? (flags & 0x80) != 0 : (flags & 2) != 0;
            }
            else if (unified != 0)
            {
                byte[] r = Request(h, device, unified, 1);
                if (r == null) return null;
                int flags = r[5];
                if (r[4] > 0) b.Percent = Math.Min((int)r[4], 100);
                else b.Level = (flags & 8) != 0 ? "full" : (flags & 4) != 0 ? "medium" : (flags & 2) != 0 ? "low" : (flags & 1) != 0 ? "empty" : "";
                b.Charging = r[6] == 1 || r[6] == 2;
                if (r[6] == 3) b.Percent = 100;
            }
            else if (status != 0)
            {
                byte[] r = Request(h, device, status, 0);
                if (r == null || r[4] == 0) return null;
                b.Percent = Math.Min((int)r[4], 100);
                b.Charging = r[6] >= 1 && r[6] <= 3;
            }
            else return null;   // no battery (wired device)
            if (b.Percent < 0 && b.Level.Length == 0) return null;

            // name and type (feature 0x0005)
            byte nameFeature = FeatureIndex(h, device, 0x0005);
            if (nameFeature != 0)
            {
                byte[] count = Request(h, device, nameFeature, 0);
                var name = new StringBuilder();
                for (int i = 0; count != null && i < count[4] && i < 64; i += 16)
                {
                    byte[] part = Request(h, device, nameFeature, 1, (byte)i);
                    if (part == null) break;
                    name.Append(Encoding.ASCII.GetString(part, 4, Math.Min(16, count[4] - i)));
                }
                b.Name = name.ToString().Trim('\0', ' ');
                byte[] type = Request(h, device, nameFeature, 2);
                if (type != null) b.Kind = type[4] == 0 ? "keyboard" : type[4] == 3 || type[4] == 4 || type[4] == 5 ? "mouse" : type[4] == 8 ? "headset" : type[4] == 11 || type[4] == 12 ? "controller" : "device";
            }
            if (string.IsNullOrEmpty(b.Name)) b.Name = "Logitech";
            return b;
        }

        // Remaining charge of a lithium battery by its voltage (typical discharge curve, as used by Solaar)
        static readonly int[,] VoltageCurve =
        {
            { 4186, 100 }, { 4067, 90 }, { 3989, 80 }, { 3922, 70 }, { 3859, 60 }, { 3811, 50 },
            { 3778, 40 }, { 3751, 30 }, { 3717, 20 }, { 3671, 10 }, { 3646, 5 }, { 3579, 2 }, { 3500, 0 }
        };

        public static int PercentFromVoltage(int millivolts)
        {
            if (millivolts >= VoltageCurve[0, 0]) return 100;
            for (int i = 1; i < VoltageCurve.GetLength(0); i++)
            {
                int v = VoltageCurve[i, 0], p = VoltageCurve[i, 1], vHigh = VoltageCurve[i - 1, 0], pHigh = VoltageCurve[i - 1, 1];
                if (millivolts >= v) return p + (int)Math.Round((millivolts - v) * (pHigh - p) / (double)(vHigh - v));
            }
            return 0;
        }

        static IEnumerable<Battery> Logitech(List<HidInterface> hid)
        {
            if (DateTime.UtcNow - logitechTime < LogitechInterval) return logitechCache;
            var result = new List<Battery>();
            // HID++ long reports: page FF00 usage 2 (USB receivers and cables), page FF43 usage 0x0202 (Bluetooth)
            foreach (var d in hid.Where(d => d.Vid == 0x046D && d.OutputLength >= 20 &&
                                             ((d.UsagePage == 0xFF00 && d.Usage == 2) || (d.UsagePage == 0xFF43 && d.Usage == 0x0202))))
            {
                using (var h = Hid.Handle.Open(d, write: true))
                {
                    if (h == null) continue;
                    foreach (byte device in FindDevices(h))
                    {
                        Battery b = LogitechDevice(h, device);
                        if (b != null) result.Add(b);
                    }
                }
            }
            // a sleeping device sometimes does not answer at all: keep the last known values for a while
            if (result.Count < logitechCache.Count && DateTime.UtcNow - logitechAnswered < TimeSpan.FromMinutes(15))
            {
                logitechTime = DateTime.UtcNow - LogitechInterval + TimeSpan.FromMinutes(1);   // try again in a minute
                return logitechCache;
            }
            logitechCache = result;
            logitechTime = DateTime.UtcNow;
            if (result.Count > 0) logitechAnswered = logitechTime;
            else logitechTime -= LogitechInterval - TimeSpan.FromMinutes(1);   // nothing found (yet): try again in a minute
            return result;
        }

        #endregion

        #region Bluetooth (level reported to Windows)

        [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public int pid; }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(IntPtr guid, string enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out int type, byte[] buffer, int size, out int required, int flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        static readonly DEVPROPKEY BatteryKey = new DEVPROPKEY { fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), pid = 2 };
        static readonly DEVPROPKEY FriendlyName = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
        static readonly DEVPROPKEY Description = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 2 };

        static IEnumerable<Battery> Bluetooth()
        {
            var result = new List<Battery>();
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 0x2 | 0x4);   // DIGCF_PRESENT | DIGCF_ALLCLASSES
            if (set == new IntPtr(-1)) return result;
            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                var buffer = new byte[512];
                string Text(DEVPROPKEY key)
                {
                    var k = key;
                    return SetupDiGetDeviceProperty(set, ref data, ref k, out int type, buffer, buffer.Length, out int n, 0) && type == 0x12 && n > 2
                        ? Encoding.Unicode.GetString(buffer, 0, n - 2) : null;   // DEVPROP_TYPE_STRING
                }
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    var key = BatteryKey;
                    if (!SetupDiGetDeviceProperty(set, ref data, ref key, out int type, buffer, buffer.Length, out int size, 0) || type != 0x3 || size < 1) continue;   // DEVPROP_TYPE_BYTE
                    int percent = buffer[0];
                    string name = Text(FriendlyName) ?? Text(Description);
                    if (string.IsNullOrEmpty(name) || percent > 100) continue;
                    result.Add(new Battery { Name = name, Percent = percent, Kind = GuessKind(name) });
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return result;
        }

        static string GuessKind(string name)
        {
            string n = name.ToLowerInvariant();
            if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("dualsense") || n.Contains("joy-con")) return "controller";
            if (n.Contains("mouse") || n.Contains("maus")) return "mouse";
            if (n.Contains("keyboard") || n.Contains("tastatur")) return "keyboard";
            if (n.Contains("headset") || n.Contains("headphone") || n.Contains("buds") || n.Contains("kopfh")) return "headset";
            return "device";
        }

        #endregion
    }
}
