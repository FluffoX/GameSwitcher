using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading;

namespace GameSwitcher
{
    // Runs the GUI (app\GameSwitcher.ps1) in-process, so no console window appears and Task Manager
    // shows "GameSwitcher"
    static class PowerShellHost
    {
        public static int RunGui(bool demo)
        {
            string script = Path.Combine(Paths.AppDir, "GameSwitcher.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException("Missing program file: " + script);

            InitialSessionState iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using (Runspace rs = RunspaceFactory.CreateRunspace(iss))
            {
                rs.ApartmentState = ApartmentState.STA;
                rs.ThreadOptions = PSThreadOptions.UseCurrentThread;   // WinForms needs this STA thread
                rs.Open();
                using (PowerShell ps = PowerShell.Create())
                {
                    ps.Runspace = rs;
                    ps.AddCommand(script);
                    if (demo) ps.AddParameter("Demo");
                    ps.Invoke();
                    if (ps.Streams.Error.Count > 0) Paths.WriteErrorLog(ps.Streams.Error[0].ToString());
                }
            }
            return 0;
        }
    }
}
