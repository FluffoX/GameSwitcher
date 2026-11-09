using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameSwitcher.Gui
{
    class Game
    {
        public string Name, Source, Path, Key, Origin, MainExe, Launch;
        public string[] Exes = new string[0];
    }

    // Finds installed games: Steam, Epic, GOG, Ubisoft, Xbox, EA, games Windows knows, custom folders and programs
    static class GameLibrary
    {
        // Folders and executables that are certainly not games
        static readonly Regex SkipDirs = new Regex(@"^(_?commonredist|redist|redistributables?|directx|dotnetfx|vcredist|easyanticheat|battleye|__installer|installers?|support|prereq.*|\$.*)$", RegexOptions.IgnoreCase);
        static readonly Regex SkipExe = new Regex(@"(unins|setup|install|redist|crash|report|dxsetup|easyanticheat|beservice|battleye|prereq|cefprocess|helper|updater|update|webview|dotnet|vulkan|physx|oalinst|benchmark|server|anticheat|overlay|injector|activationui|debugger|bssndrpt|checkenv|worldbuilder|^cdb$|^7z|^8za)", RegexOptions.IgnoreCase);

        static bool IsGameExe(string file) => !SkipExe.IsMatch(System.IO.Path.GetFileNameWithoutExtension(file));
        static bool IsGameDir(string dir) => !SkipDirs.IsMatch(System.IO.Path.GetFileName(dir));

        static List<string> FindExes(string dir, int depth = 0)
        {
            var result = new List<string>();
            try
            {
                result.AddRange(Directory.EnumerateFiles(dir, "*.exe").Where(IsGameExe));
                if (depth < 5)
                    foreach (string d in Directory.EnumerateDirectories(dir).Where(IsGameDir)) result.AddRange(FindExes(d, depth + 1));
            }
            catch { }
            return result;
        }

        // A game in a folder; launch: store URI or program used for "Launch game" (fallback: the largest executable)
        static Game FromFolder(string name, string source, string path, string origin, string launch = null)
        {
            var files = FindExes(path);
            if (files.Count == 0) return null;
            string main = files.OrderByDescending(f => { try { return new FileInfo(f).Length; } catch { return 0; } }).First();
            if (launch == null && source == "Xbox")
            {
                string helper = System.IO.Path.Combine(path, "Content", "gamelaunchhelper.exe");
                if (File.Exists(helper)) launch = helper;
            }
            return new Game
            {
                Name = name, Source = source, Path = path, Key = path.TrimEnd('\\').ToLowerInvariant(), Origin = origin,
                Exes = files.Select(System.IO.Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                MainExe = main, Launch = launch
            };
        }

        public static Game FromExe(string exe, string source = "Custom", string key = null)
        {
            string name = null;
            try { name = FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim(); } catch { }
            if (string.IsNullOrEmpty(name)) name = System.IO.Path.GetFileNameWithoutExtension(exe);
            return new Game
            {
                Name = name, Source = source, Path = exe, Key = key ?? exe.ToLowerInvariant(), Origin = exe,
                Exes = new[] { System.IO.Path.GetFileName(exe) }, MainExe = exe, Launch = exe
            };
        }

        static IEnumerable<Game> Steam()
        {
            string steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                        ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
            if (string.IsNullOrEmpty(steam)) yield break;
            steam = steam.Replace('/', '\\');
            var libs = new List<string> { steam };
            string vdf = System.IO.Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
                libs.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\"").Cast<Match>().Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
            foreach (string lib in libs.Select(l => l.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string apps = System.IO.Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (string acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    string text = File.ReadAllText(acf);
                    string name = Regex.Match(text, "\"name\"\\s+\"(.+?)\"").Groups[1].Value;
                    string dir = Regex.Match(text, "\"installdir\"\\s+\"(.+?)\"").Groups[1].Value;
                    string appId = Regex.Match(System.IO.Path.GetFileName(acf), @"\d+").Value;
                    if (dir.Length == 0 || Regex.IsMatch(name, "^(Steamworks Common Redistributables|Proton|Steam Linux Runtime|SteamVR)")) continue;
                    string path = System.IO.Path.Combine(lib, "steamapps", "common", dir);
                    if (Directory.Exists(path)) yield return FromFolder(name, "Steam", path, "Steam", "steam://rungameid/" + appId);
                }
            }
        }

        static IEnumerable<Game> Epic()
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (!Directory.Exists(dir)) yield break;
            foreach (string f in Directory.GetFiles(dir, "*.item"))
            {
                var m = Json.ReadFile(f);
                if (m == null || m.Bool("bIsIncompleteInstall")) continue;
                var categories = m.Strings("AppCategories");
                if (categories.Length > 0 && !categories.Contains("games")) continue;
                string location = m.Str("InstallLocation");
                if (location.Length == 0 || !Directory.Exists(location)) continue;
                string uri = m.Str("CatalogNamespace").Length > 0 && m.Str("CatalogItemId").Length > 0
                    ? $"com.epicgames.launcher://apps/{m.Str("CatalogNamespace")}%3A{m.Str("CatalogItemId")}%3A{m.Str("AppName")}?action=launch&silent=true"
                    : $"com.epicgames.launcher://apps/{m.Str("AppName")}?action=launch&silent=true";
                yield return FromFolder(m.Str("DisplayName"), "Epic", location, "Epic", uri);
            }
        }

        static IEnumerable<Game> FromRegistry(string keyPath, Func<RegistryKey, string, Game> make)
        {
            using (var root = Registry.LocalMachine.OpenSubKey(keyPath))
                foreach (string sub in root?.GetSubKeyNames() ?? new string[0])
                    using (var k = root.OpenSubKey(sub))
                        if (k != null) yield return make(k, sub);
        }

        static IEnumerable<Game> Gog() => FromRegistry(@"SOFTWARE\WOW6432Node\GOG.com\Games", (k, id) =>
        {
            string path = k.GetValue("path") as string;
            return !string.IsNullOrEmpty(path) && Directory.Exists(path) ? FromFolder(k.GetValue("gameName") as string ?? id, "GOG", path, "GOG", k.GetValue("exe") as string) : null;
        });

        static IEnumerable<Game> Ubisoft() => FromRegistry(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs", (k, id) =>
        {
            string path = (k.GetValue("InstallDir") as string)?.Replace('/', '\\').TrimEnd('\\');
            return !string.IsNullOrEmpty(path) && Directory.Exists(path) ? FromFolder(System.IO.Path.GetFileName(path), "Ubisoft", path, "Ubisoft", $"uplay://launch/{id}/0") : null;
        });

        // Every subfolder is a game; if executables sit directly in the folder, the folder itself is the game
        public static List<Game> FromGamesFolder(string folder, string source)
        {
            var result = new List<Game>();
            if (!Directory.Exists(folder)) return result;
            try
            {
                if (Directory.EnumerateFiles(folder, "*.exe").Any(IsGameExe)) return new List<Game> { FromFolder(System.IO.Path.GetFileName(folder.TrimEnd('\\')), source, folder, folder) }.Where(g => g != null).ToList();
                foreach (string d in Directory.EnumerateDirectories(folder).Where(IsGameDir))
                {
                    Game g = FromFolder(System.IO.Path.GetFileName(d), source, d, folder);
                    if (g != null) result.Add(g);
                }
            }
            catch { }
            return result;
        }

        static IEnumerable<Game> Xbox() =>
            DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed)
                .SelectMany(d => FromGamesFolder(System.IO.Path.Combine(d.RootDirectory.FullName, "XboxGames"), "Xbox"));

        // Games Windows has seen running (Game Bar / Game Mode); same keys as the service's automatic detection
        static IEnumerable<Game> WindowsKnown()
        {
            using (var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children"))
                foreach (string sub in children?.GetSubKeyNames() ?? new string[0])
                    using (var k = children.OpenSubKey(sub))
                        if (k?.GetValue("MatchedExeFullPath") is string path && File.Exists(path))
                            yield return FromExe(path, "Windows", "auto:" + path.ToLowerInvariant());
        }

        static IEnumerable<Game> Safe(Func<IEnumerable<Game>> source)
        {
            try { return source().Where(g => g != null).ToList(); } catch { return new List<Game>(); }
        }

        public static List<Game> FindAll(IEnumerable<string> customFolders, IEnumerable<string> customExes, IEnumerable<GameRef> selected)
        {
            var all = new List<Game>();
            all.AddRange(Safe(Steam));
            all.AddRange(Safe(Epic));
            all.AddRange(Safe(Gog));
            all.AddRange(Safe(Ubisoft));
            all.AddRange(Safe(Xbox));
            all.AddRange(FromGamesFolder(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EA Games"), "EA"));
            foreach (string f in customFolders) all.AddRange(FromGamesFolder(f, "Folder"));
            foreach (string e in customExes) if (File.Exists(e)) all.Add(FromExe(e));
            var knownExes = new HashSet<string>(all.SelectMany(g => g.Exes), StringComparer.OrdinalIgnoreCase);
            all.AddRange(Safe(WindowsKnown).Where(g => !knownExes.Contains(g.Exes[0])));

            var found = new Dictionary<string, Game>();
            foreach (Game g in all) if (!found.ContainsKey(g.Key)) found[g.Key] = g;
            foreach (GameRef s in selected)
            {
                if (found.TryGetValue(s.Key, out Game g)) s.Exes = g.Exes;   // refresh the executable list after game updates
                else found[s.Key] = new Game { Name = s.Name, Source = "Missing", Path = "", Key = s.Key, Exes = s.Exes };
            }
            return found.Values.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }
}
