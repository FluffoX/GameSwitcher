// GameSwitcher.exe
//   GameSwitcher.exe              opens the window
//   GameSwitcher.exe --watcher    runs the background service with the tray icon
//   GameSwitcher.exe --demo       opens the window with sample data (screenshots); nothing is saved
//   GameSwitcher.exe --install    sets up autostart and the Start menu entry and starts the service
//   GameSwitcher.exe --uninstall  stops the service and removes autostart and the Start menu entry
//   GameSwitcher.exe --stop       asks the service to revert its changes and exit (used by the setup)
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using GameSwitcher.Gui;

namespace GameSwitcher
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);

        [STAThread]
        static int Main(string[] args)
        {
            bool HasArg(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            try
            {
                Paths.Prepare();
                if (HasArg("--install")) { Autostart.Enable(startNow: true); return 0; }
                if (HasArg("--uninstall")) { Autostart.Uninstall(); return 0; }
                if (HasArg("--stop")) { Autostart.StopService(); return 0; }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (HasArg("--watcher")) return Watcher.Run();
                return RunWindow(HasArg("--demo"));
            }
            catch (Exception ex)
            {
                Paths.WriteErrorLog(ex.ToString());
                if (!HasArg("--watcher") && !HasArg("--uninstall") && !HasArg("--stop") && !HasArg("--install")) MessageBox.Show(ex.Message, "GameSwitcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        static int RunWindow(bool demo)
        {
            // One window per installation folder: bring an open one to the front
            Mutex mutex = null;
            if (!demo)
            {
                string id = BitConverter.ToString(MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(Paths.InstallDir.ToLowerInvariant()))).Replace("-", "");
                mutex = new Mutex(true, @"Local\GameSwitcherGui-" + id, out bool createdNew);
                if (!createdNew)
                {
                    IntPtr h = FindWindow(null, "GameSwitcher");
                    if (h != IntPtr.Zero) { ShowWindow(h, 9); SetForegroundWindow(h); }
                    return 0;
                }
            }

            Theme.AllowDarkMode();
            GuiSettings settings;
            var playtime = (System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, object>>)null;
            if (demo) settings = MainForm.DemoSettings(out playtime);
            else settings = GuiSettings.Load();
            if (settings.Language.Length > 0) Strings.LoadCode(settings.Language);
            else Strings.Load(new System.Collections.Generic.Dictionary<string, object>());

            // The legacy games.txt (version 1.x) is no longer used
            if (!demo) try { File.Delete(Path.Combine(Paths.InstallDir, "games.txt")); } catch { }

            using (var form = new MainForm(settings, demo))
            {
                if (demo) form.SetDemoPlaytime(playtime);
                Application.Run(form);
            }
            GC.KeepAlive(mutex);
            return 0;
        }
    }
}
