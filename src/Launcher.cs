using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace GameSwitcher
{
    // Starting a game through GameSwitcher: the service applies the game's profile first and then starts
    // the game, so games that check HDR or the resolution at startup already see the right settings.
    // Used by the window, the tray menu and desktop shortcuts (GameSwitcher.exe --launch "<game key>").
    static class Launcher
    {
        public const string RequestEvent = @"Local\GameSwitcherLaunch";
        static string RequestPath => Paths.Data("launch.json");

        // What to start for a game: its store URI or program from the game list, or the path of a game Windows knows
        public static string TargetFor(Config config, string key)
        {
            string launch = config.Library.FirstOrDefault(g => g.Key == key)?.Launch;
            if (string.IsNullOrEmpty(launch) && key.StartsWith("auto:")) launch = key.Substring(5);
            if (string.IsNullOrEmpty(launch) && File.Exists(key)) launch = key;
            return string.IsNullOrEmpty(launch) ? null : launch;
        }

        public static void Start(string target)
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (File.Exists(target)) psi.WorkingDirectory = Path.GetDirectoryName(target);
            Process.Start(psi);
        }

        // Hands the game to the service (starting the service if needed); without it the game starts directly
        public static void Request(string key, string fallbackTarget)
        {
            Autostart.StartService();   // does nothing if it already runs
            // the service accepts requests a moment after it has started
            EventWaitHandle signal = null;
            for (int i = 0; i < 40 && !EventWaitHandle.TryOpenExisting(RequestEvent, out signal); i++) Thread.Sleep(200);
            if (signal != null)
            {
                using (signal)
                {
                    Json.WriteFile(RequestPath, new Dictionary<string, object> { ["Key"] = key, ["Time"] = DateTime.Now.ToString("o") });
                    signal.Set();
                }
                return;
            }
            if (!string.IsNullOrEmpty(fallbackTarget)) Start(fallbackTarget);
        }

        // Service: the requested game key (only requests of the last 30 seconds count)
        public static string TakeRequest()
        {
            var r = Json.ReadFile(RequestPath);
            try { File.Delete(RequestPath); } catch { }
            if (r == null || !DateTime.TryParse(r.Str("Time"), null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t)) return null;
            return (DateTime.Now - t).TotalSeconds < 30 ? r.Str("Key") : null;
        }

        // A desktop shortcut that starts the game through GameSwitcher; returns its path
        public static string CreateShortcut(string name, string key, string icon)
        {
            string safe = string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), safe + ".lnk");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = Paths.Exe;
            link.Arguments = "--launch \"" + key + "\"";
            link.WorkingDirectory = Paths.InstallDir;
            link.IconLocation = (File.Exists(icon) ? icon : Paths.Exe) + ",0";
            link.Description = name + " (GameSwitcher)";
            link.Save();
            return path;
        }
    }
}
