// GameSwitcher.exe: hosts the PowerShell scripts in-process, so Task Manager shows "GameSwitcher"
// with the app icon instead of "Windows PowerShell", and no console window ever appears.
//   GameSwitcher.exe            opens the GUI (GameSwitcher.ps1)
//   GameSwitcher.exe --watcher  runs the background service with the tray icon (Watcher.ps1)
//   GameSwitcher.exe --demo     opens the GUI with sample data
// Built by Build.ps1 with the C# compiler that ships with Windows.
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
        string script = Path.Combine(root, watcher ? "Watcher.ps1" : "GameSwitcher.ps1");

        try {
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
                    if (ps.Streams.Error.Count > 0) WriteErrorLog(root, ps.Streams.Error[0].ToString());
                }
            }
        } catch (Exception ex) {
            WriteErrorLog(root, ex.ToString());
            if (!watcher) MessageBox.Show(ex.Message, "GameSwitcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        return 0;
    }

    static bool HasArg(string[] args, string name) {
        foreach (string a in args) if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static void WriteErrorLog(string root, string text) {
        try { File.AppendAllText(Path.Combine(root, "error.log"), DateTime.Now.ToString("s") + "  " + text + Environment.NewLine); } catch { }
    }
}
