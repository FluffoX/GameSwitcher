using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace GameSwitcher.Native
{
    // A HID interface (one top-level collection of a device)
    class HidInterface
    {
        public string Path, Product;
        public int Vid, Pid, UsagePage, Usage, InputLength, OutputLength, FeatureLength;
        // Standard HID battery value (Generic Device Controls: Battery Strength), if the device has one
        public int BatteryReportType = -1, BatteryReportId;
    }

    // Enumerating HID devices and reading/writing reports with timeouts (no exclusive access needed)
    static class Hid
    {
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }
        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                          NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                          NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder buffer, int length);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, string enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, int length, IntPtr read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, IntPtr buffer, int length, IntPtr written, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out int transferred, bool wait);
        [DllImport("kernel32.dll")] static extern bool CancelIo(SafeFileHandle h);

        const int DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10, ERROR_IO_PENDING = 997;
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, SHARE_RW = 3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

        public static List<HidInterface> Enumerate()
        {
            var result = new List<HidInterface>();
            HidD_GetHidGuid(out Guid guid);
            IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return result;
            try
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA)) };
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
                {
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                    if (size <= 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);   // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero)) continue;
                        HidInterface info = Describe(Marshal.PtrToStringUni(detail + 4));
                        if (info != null) result.Add(info);
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return result;
        }

        // Opening without access rights is allowed for every HID device, also keyboards and mice
        static HidInterface Describe(string path)
        {
            using (SafeFileHandle h = CreateFile(path, 0, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES)) };
                if (!HidD_GetAttributes(h, ref attr)) return null;
                var info = new HidInterface { Path = path, Vid = attr.VendorID, Pid = attr.ProductID };
                if (HidD_GetPreparsedData(h, out IntPtr pre))
                {
                    try
                    {
                        if (HidP_GetCaps(pre, out HIDP_CAPS caps) == 0x110000)   // HIDP_STATUS_SUCCESS
                        {
                            info.UsagePage = caps.UsagePage; info.Usage = caps.Usage;
                            info.InputLength = caps.InputReportByteLength; info.OutputLength = caps.OutputReportByteLength;
                            info.FeatureLength = caps.FeatureReportByteLength;
                            FindBatteryValue(info, pre, caps);
                        }
                    }
                    finally { HidD_FreePreparsedData(pre); }
                }
                var name = new StringBuilder(128);
                if (HidD_GetProductString(h, name, name.Capacity * 2)) info.Product = name.ToString().Trim();
                return info;
            }
        }


        #region Standard HID battery value

        const int HidP_Input = 0, HidP_Feature = 2, HIDP_STATUS_SUCCESS = 0x110000;
        const ushort GenericDeviceControls = 0x06, BatteryStrength = 0x20;
        [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort length, IntPtr preparsed);
        [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int type, ushort usagePage, ushort link, ushort usage, out uint value, IntPtr preparsed, byte[] report, int length);
        [DllImport("hid.dll")] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
        const int ValueCapsSize = 72;   // sizeof(HIDP_VALUE_CAPS)

        // Looks for "Battery Strength" among the feature and input values (the standard way a HID device reports its battery)
        static void FindBatteryValue(HidInterface info, IntPtr pre, HIDP_CAPS caps)
        {
            foreach (var (type, count) in new[] { (HidP_Feature, (int)caps.NumberFeatureValueCaps), (HidP_Input, (int)caps.NumberInputValueCaps) })
            {
                if (count == 0) continue;
                var buffer = new byte[count * ValueCapsSize];
                ushort n = (ushort)count;
                if (HidP_GetValueCaps(type, buffer, ref n, pre) != HIDP_STATUS_SUCCESS) continue;
                for (int i = 0; i < n; i++)
                {
                    int o = i * ValueCapsSize;
                    ushort page = BitConverter.ToUInt16(buffer, o);
                    bool isRange = buffer[o + 12] != 0;
                    ushort usageMin = BitConverter.ToUInt16(buffer, o + 56), usageMax = isRange ? BitConverter.ToUInt16(buffer, o + 58) : usageMin;
                    if (page != GenericDeviceControls || BatteryStrength < usageMin || BatteryStrength > usageMax) continue;
                    info.BatteryReportType = type;
                    info.BatteryReportId = buffer[o + 2];
                    return;
                }
            }
        }

        // Reads the standard battery value as a percentage (-1: not available)
        public static int ReadBatteryStrength(HidInterface d)
        {
            if (d.BatteryReportType < 0) return -1;
            bool feature = d.BatteryReportType == HidP_Feature;
            // feature reports can be read without access rights; input reports need a read handle
            IntPtr pre;
            using (SafeFileHandle h = CreateFile(d.Path, 0, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                if (h.IsInvalid || !HidD_GetPreparsedData(h, out pre)) return -1;
            try
            {
                if (feature)
                {
                    if (d.FeatureLength <= 0) return -1;
                    var report = new byte[d.FeatureLength];
                    report[0] = (byte)d.BatteryReportId;
                    using (SafeFileHandle h = CreateFile(d.Path, 0, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                        if (h.IsInvalid || !HidD_GetFeature(h, report, report.Length)) return -1;
                    return Percent(pre, HidP_Feature, report);
                }
                // input reports: the device sends them on its own (several report IDs may alternate)
                using (var reader = Handle.Open(d))
                {
                    if (reader == null) return -1;
                    for (int tries = 0; tries < 6; tries++)
                    {
                        byte[] report = reader.Read(300);
                        if (report == null) return -1;
                        if (d.BatteryReportId != 0 && report[0] != d.BatteryReportId) continue;
                        int p = Percent(pre, HidP_Input, report);
                        if (p >= 0) return p;
                    }
                }
                return -1;
            }
            finally { HidD_FreePreparsedData(pre); }
        }

        static int Percent(IntPtr pre, int type, byte[] report)
        {
            if (HidP_GetUsageValue(type, GenericDeviceControls, 0, BatteryStrength, out uint value, pre, report, report.Length) != HIDP_STATUS_SUCCESS) return -1;
            // the value range comes from the report descriptor; most devices use 0..100 or 0..255
            int min = 0, max = 100;
            var caps = new byte[ValueCapsSize * 64];
            ushort n = 64;
            if (HidP_GetValueCaps(type, caps, ref n, pre) == HIDP_STATUS_SUCCESS)
                for (int i = 0; i < n; i++)
                {
                    int o = i * ValueCapsSize;
                    if (BitConverter.ToUInt16(caps, o) != GenericDeviceControls) continue;
                    min = BitConverter.ToInt32(caps, o + 40); max = BitConverter.ToInt32(caps, o + 44);
                    break;
                }
            if (max <= min) { min = 0; max = 100; }
            return PercentOf((int)value, min, max);
        }

        public static int PercentOf(int value, int min, int max) =>
            Math.Max(0, Math.Min(100, (int)Math.Round((value - min) * 100.0 / (max - min))));

        #endregion
        // An open HID interface for reading and writing reports with timeouts
        public sealed class Handle : IDisposable
        {
            readonly SafeFileHandle h;
            readonly int inLength, outLength;
            readonly IntPtr buffer, overlapped;
            readonly ManualResetEvent done = new ManualResetEvent(false);
            static readonly int OverlappedSize = Marshal.SizeOf(typeof(NativeOverlapped));

            Handle(SafeFileHandle h, int inLength, int outLength)
            {
                this.h = h; this.inLength = inLength; this.outLength = outLength;
                buffer = Marshal.AllocHGlobal(Math.Max(inLength, outLength) + 1);
                overlapped = Marshal.AllocHGlobal(OverlappedSize);
            }

            public static Handle Open(HidInterface device, bool write = false)
            {
                SafeFileHandle h = CreateFile(device.Path, GENERIC_READ | (write ? GENERIC_WRITE : 0), SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (h.IsInvalid) { h.Dispose(); return null; }
                return new Handle(h, device.InputLength, device.OutputLength);
            }

            // Starts an overlapped read or write and waits for it; cancels it after the timeout
            bool Transfer(bool read, int length, int timeoutMs, out int transferred)
            {
                transferred = 0;
                done.Reset();
                var ov = new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() };
                Marshal.StructureToPtr(ov, overlapped, false);
                bool ok = read ? ReadFile(h, buffer, length, IntPtr.Zero, overlapped) : WriteFile(h, buffer, length, IntPtr.Zero, overlapped);
                if (!ok && Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return false;
                if (!done.WaitOne(timeoutMs))
                {
                    CancelIo(h);
                    GetOverlappedResult(h, overlapped, out _, true);   // wait until the cancelled request has finished
                    return false;
                }
                return GetOverlappedResult(h, overlapped, out transferred, false);
            }

            public byte[] Read(int timeoutMs)
            {
                if (inLength <= 0 || !Transfer(true, inLength, timeoutMs, out int n) || n <= 0) return null;
                var data = new byte[n];
                Marshal.Copy(buffer, data, 0, n);
                return data;
            }

            public bool Write(byte[] report, int timeoutMs)
            {
                if (outLength <= 0) return false;
                var data = new byte[outLength];
                Array.Copy(report, data, Math.Min(report.Length, outLength));
                Marshal.Copy(data, 0, buffer, data.Length);
                return Transfer(false, data.Length, timeoutMs, out _);
            }

            public void Dispose()
            {
                h.Dispose();
                Marshal.FreeHGlobal(buffer);
                Marshal.FreeHGlobal(overlapped);
                done.Dispose();
            }
        }
    }
}
