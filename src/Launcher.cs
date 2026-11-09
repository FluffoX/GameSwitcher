// GameSwitcher.exe: hosts the PowerShell scripts in app\ in-process, so Task Manager shows "GameSwitcher"
// with the app icon instead of "Windows PowerShell", and no console window ever appears.
//   GameSwitcher.exe            opens the GUI (app\GameSwitcher.ps1)
//   GameSwitcher.exe --watcher  runs the background service with the tray icon (app\Watcher.ps1)
//   GameSwitcher.exe --demo     opens the GUI with sample data
// Built by Build.ps1 with the C# compiler that ships with Windows; version details come from a
// file Build.ps1 generates from app\AppInfo.ps1.
using System;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("GameSwitcher")]
[assembly: AssemblyProduct("GameSwitcher")]
[assembly: AssemblyDescription("Switches HDR, audio and more when a game starts")]

static class Program {
    [STAThread]
    static int Main(string[] args) {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        bool watcher = HasArg(args, "--watcher");
        string script = Path.Combine(root, "app", watcher ? "Watcher.ps1" : "GameSwitcher.ps1");

        try {
            if (!File.Exists(script)) throw new FileNotFoundException("Missing program file: " + script);
            InitialSessionState iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using (Runspace rs = RunspaceFactory.CreateRunspace(iss)) {
                rs.ApartmentState = ApartmentState.STA;
                rs.ThreadOptions = PSThreadOptions.UseCurrentThread;   // WinForms needs this STA thread
                rs.Open();
                using (PowerShell ps = PowerShell.Create()) {
                    ps.Runspace = rs;
                    ps.AddCommand(script);
                    if (HasArg(args, "--demo")) ps.AddParameter("Demo");
                    ps.Invoke();
                    if (ps.Streams.Error.Count > 0) WriteErrorLog(ps.Streams.Error[0].ToString());
                }
            }
        } catch (Exception ex) {
            WriteErrorLog(ex.ToString());
            if (!watcher) MessageBox.Show(ex.Message, "GameSwitcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        return 0;
    }

    static bool HasArg(string[] args, string name) {
        foreach (string a in args) if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Same data folder the scripts use (%AppData%\GameSwitcher)
    static void WriteErrorLog(string text) {
        try {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameSwitcher");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), DateTime.Now.ToString("s") + "  " + text + Environment.NewLine);
        } catch { }
    }
}
