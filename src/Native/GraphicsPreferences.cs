using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace GameSwitcher.Native
{
    // Per-program graphics settings, as in Windows Settings → System → Display → Graphics:
    // HKCU\Software\Microsoft\DirectX\UserGpuPreferences, one value per program path, e.g. "GpuPreference=2;AutoHDREnable=2097;".
    // Windows reads them when the program starts.
    static class GraphicsPreferences
    {
        const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
        public const string AutoHdrOn = "2097", AutoHdrOff = "2096";   // values Windows Settings writes for the per-app switch

        public static string Read(string program)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                return k?.GetValue(program) as string;
        }

        // null removes the entry
        public static void Write(string program, string value)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (value == null) k.DeleteValue(program, false);
                else k.SetValue(program, value, RegistryValueKind.String);
            }
        }

        // The original value with GameSwitcher's settings applied; other settings are kept.
        // gpu: high performance; autoHdr: "on", "off" or anything else for the Windows default.
        public static string Compose(string original, bool gpu, string autoHdr)
        {
            var tokens = (original ?? "").Split(';').Where(t => t.Contains("="))
                .Select(t => new KeyValuePair<string, string>(t.Substring(0, t.IndexOf('=')).Trim(), t.Substring(t.IndexOf('=') + 1).Trim())).ToList();
            void Set(string name, string value)
            {
                int i = tokens.FindIndex(t => t.Key == name);
                var entry = new KeyValuePair<string, string>(name, value);
                if (i >= 0) tokens[i] = entry; else tokens.Add(entry);
            }
            if (gpu) Set("GpuPreference", "2");
            if (autoHdr == "on") Set("AutoHDREnable", AutoHdrOn);
            else if (autoHdr == "off") Set("AutoHDREnable", AutoHdrOff);
            return tokens.Count == 0 ? null : string.Concat(tokens.Select(t => t.Key + "=" + t.Value + ";"));
        }
    }
}
