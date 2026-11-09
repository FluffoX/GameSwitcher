using System;
using System.IO;
using System.Linq;
using System.Text;

namespace GameSwitcher
{
    // watcher.log in the data folder, shown by the GUI's "Show log" button
    static class Log
    {
        static string FilePath => Paths.Data("watcher.log");

        public static void Write(string message)
        {
            try { File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}\r\n", new UTF8Encoding(false)); }
            catch { }
        }

        // Keeps the log small: the last 500 lines once it grows beyond 256 KB
        public static void Trim()
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (!info.Exists || info.Length <= 256 * 1024) return;
                var lines = File.ReadAllLines(FilePath, Encoding.UTF8);
                File.WriteAllLines(FilePath, lines.Skip(Math.Max(0, lines.Length - 500)), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
