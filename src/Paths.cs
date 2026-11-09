using System;
using System.IO;

namespace GameSwitcher
{
    // Program folder (GameSwitcher.exe, app\) and data folder (%AppData%\GameSwitcher)
    static class Paths
    {
        public static readonly string InstallDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static readonly string AppDir = Path.Combine(InstallDir, "app");
        // GAMESWITCHER_DATA can point somewhere else (tests, portable use)
        public static readonly string DataDir = Environment.GetEnvironmentVariable("GAMESWITCHER_DATA") is string custom && custom.Length > 0
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameSwitcher");
        public static readonly string Exe = Path.Combine(InstallDir, "GameSwitcher.exe");

        public static string Data(string name) => Path.Combine(DataDir, name);

        // Creates the data folder and moves files of versions before 2.2 out of the program folder
        public static void Prepare()
        {
            Directory.CreateDirectory(DataDir);
            foreach (string name in new[] { "settings.json", "playtime.json", "state.json", "pause.json", "watcher.log", "error.log" })
            {
                string oldPath = Path.Combine(InstallDir, name), newPath = Data(name);
                if (File.Exists(oldPath) && !File.Exists(newPath))
                {
                    try { File.Move(oldPath, newPath); } catch { }
                }
            }
        }

        public static void WriteErrorLog(string text)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(Data("error.log"), DateTime.Now.ToString("s") + "  " + text + Environment.NewLine);
            }
            catch { }
        }
    }
}
