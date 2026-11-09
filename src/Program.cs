// GameSwitcher.exe
//   GameSwitcher.exe            opens the GUI (app\GameSwitcher.ps1, hosted in-process)
//   GameSwitcher.exe --watcher  runs the background service with the tray icon (C#)
//   GameSwitcher.exe --demo     opens the GUI with sample data
using System;
using System.Linq;
using System.Windows.Forms;

namespace GameSwitcher
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool HasArg(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            try
            {
                Paths.Prepare();
                if (HasArg("--watcher"))
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    return Watcher.Run();
                }
                return PowerShellHost.RunGui(HasArg("--demo"));
            }
            catch (Exception ex)
            {
                Paths.WriteErrorLog(ex.ToString());
                if (!HasArg("--watcher")) MessageBox.Show(ex.Message, "GameSwitcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
