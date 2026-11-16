using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using GameSwitcher.Native;

namespace GameSwitcher
{
    class GraphicsData
    {
        // Program paths seen running per game key (the service records them on game start)
        public Dictionary<string, List<string>> Seen { get; set; } = new Dictionary<string, List<string>>();
        // Entries GameSwitcher wrote: program path -> what was there before and what was written
        public Dictionary<string, GraphicsEntry> Managed { get; set; } = new Dictionary<string, GraphicsEntry>();
    }
    class GraphicsEntry { public string Key { get; set; } public string Original { get; set; } public string Written { get; set; } }

    // Keeps the per-program graphics settings (GPU, Auto HDR) in line with the profiles. Windows reads them
    // when a program starts, so they are written ahead: by the window for all selected games, and by the
    // service for programs it sees running for the first time. Entries are restored when no profile wants
    // them anymore; an entry changed by the user in Windows Settings is left alone.
    static class GraphicsSync
    {
        static string FilePath => Paths.Data("graphics.json");

        static GraphicsData Load()
        {
            var d = Json.ReadFile<GraphicsData>(FilePath) ?? new GraphicsData();
            d.Seen = new Dictionary<string, List<string>>(d.Seen ?? new Dictionary<string, List<string>>(), StringComparer.OrdinalIgnoreCase);
            d.Managed = new Dictionary<string, GraphicsEntry>(d.Managed ?? new Dictionary<string, GraphicsEntry>(), StringComparer.OrdinalIgnoreCase);
            return d;
        }

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int id);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

        // Full path of a process, also for elevated ones (limited query rights suffice)
        public static string ProcessPath(Process p)
        {
            IntPtr h = OpenProcess(0x1000, false, p.Id);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try
            {
                var name = new StringBuilder(1024);
                int size = name.Capacity;
                return QueryFullProcessImageName(h, 0, name, ref size) ? name.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        // Service: remembers the running programs of a game and writes their settings right away
        public static void GameStarted(Config config, string key, IEnumerable<string> paths)
        {
            var list = paths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) return;
            var data = Load();
            if (!data.Seen.TryGetValue(key, out var seen)) data.Seen[key] = seen = new List<string>();
            bool changed = false;
            foreach (string p in list)
                if (!seen.Contains(p, StringComparer.OrdinalIgnoreCase)) { seen.Add(p); changed = true; }
            changed |= Apply(data, config, new Dictionary<string, IEnumerable<string>> { [key] = list }, full: false);
            if (changed) Json.WriteFile(FilePath, data);
        }

        // Window: games = every game that should be covered (key -> its program paths)
        public static void Sync(Config config, Dictionary<string, IEnumerable<string>> games)
        {
            var data = Load();
            if (Apply(data, config, games, full: true)) Json.WriteFile(FilePath, data);
        }

        static bool Apply(GraphicsData data, Config config, Dictionary<string, IEnumerable<string>> games, bool full)
        {
            bool changed = false;
            var wanted = new Dictionary<string, (string Key, string Value)>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in games)
            {
                ActionSet a = config.ActionsFor(game.Key);
                bool gpu = a.On("Gpu");
                string hdr = a["AutoHdr"].Str("Mode");
                if (!gpu && hdr != "on" && hdr != "off") continue;
                data.Seen.TryGetValue(game.Key, out var seen);
                foreach (string path in game.Value.Concat(seen ?? new List<string>()))
                {
                    if (string.IsNullOrEmpty(path) || wanted.ContainsKey(path)) continue;
                    string original = data.Managed.TryGetValue(path, out var m) && GraphicsPreferences.Read(path) == m.Written ? m.Original : GraphicsPreferences.Read(path);
                    wanted[path] = (game.Key, GraphicsPreferences.Compose(original, gpu, hdr));
                }
            }

            // restore entries no profile wants anymore; only with the full list (the window), the service only adds
            foreach (var m in data.Managed.ToList())
            {
                if (!full || wanted.ContainsKey(m.Key)) continue;
                try { if (GraphicsPreferences.Read(m.Key) == m.Value.Written) GraphicsPreferences.Write(m.Key, m.Value.Original); }
                catch (Exception ex) { Log.Write(Strings.T("log.actionError", "GPU", ex.Message)); }
                data.Managed.Remove(m.Key);
                changed = true;
            }

            foreach (var w in wanted)
            {
                try
                {
                    string current = GraphicsPreferences.Read(w.Key);
                    bool ours = data.Managed.TryGetValue(w.Key, out var m) && current == m.Written;
                    string original = ours ? m.Original : current;
                    if (current != w.Value.Value) GraphicsPreferences.Write(w.Key, w.Value.Value);
                    if (!ours || m.Written != w.Value.Value || m.Key != w.Value.Key)
                    {
                        data.Managed[w.Key] = new GraphicsEntry { Key = w.Value.Key, Original = original, Written = w.Value.Value };
                        changed = true;
                    }
                }
                catch (Exception ex) { Log.Write(Strings.T("log.actionError", "GPU", ex.Message)); }
            }
            return changed;
        }

        public static IEnumerable<string> SeenKeys() => Load().Seen.Keys.ToList();

        // Is a path a plausible program for a per-program entry?
        public static bool IsProgram(string path) =>
            !string.IsNullOrEmpty(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(path);
    }
}
