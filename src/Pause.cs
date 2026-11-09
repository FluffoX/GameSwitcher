using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management;
using static GameSwitcher.Strings;

namespace GameSwitcher
{
    // pause.json, shared by the window and the service:
    // { "Until": "<ISO time>" } or { "UntilReboot": true, "BootTime": "<ISO time>" }
    static class Pause
    {
        public static readonly DateTime BootTime = GetBootTime();
        static string FilePath => Paths.Data("pause.json");

        static DateTime GetBootTime()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem"))
                    foreach (ManagementObject os in searcher.Get())
                        return ManagementDateTimeConverter.ToDateTime((string)os["LastBootUpTime"]);
            }
            catch { }
            return DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount);
        }

        static DateTime? ParseDate(string s) =>
            DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime d) ? d.ToLocalTime() : (DateTime?)null;

        // The active pause, or null (an expired pause file is removed)
        public static Dictionary<string, object> Get()
        {
            var p = Json.ReadFile(FilePath);
            if (p == null) return null;
            if (p.Bool("UntilReboot") && ParseDate(p.Str("BootTime")) is DateTime boot && Math.Abs((boot - BootTime).TotalSeconds) < 5) return p;
            if (ParseDate(p.Str("Until")) is DateTime until && until > DateTime.Now) return p;
            Clear();
            return null;
        }

        public static void Set(int hours, bool untilReboot = false)
        {
            var p = untilReboot
                ? new Dictionary<string, object> { ["UntilReboot"] = true, ["BootTime"] = BootTime.ToString("o") }
                : new Dictionary<string, object> { ["Until"] = DateTime.Now.AddHours(hours).ToString("o") };
            Json.WriteFile(FilePath, p);
        }

        public static void Clear()
        {
            try { File.Delete(FilePath); } catch { }
        }

        public static string Describe(Dictionary<string, object> p) =>
            p == null ? "" : p.Bool("UntilReboot") ? T("pause.untilReboot") : T("pause.until", ParseDate(p.Str("Until"))?.ToString("t"));
    }
}
