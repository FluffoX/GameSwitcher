using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace GameSwitcher
{
    // Autostart (a logon task that runs the background service), the Start menu entry and the service itself
    static class Autostart
    {
        const string TaskName = "GameSwitcher";
        public const string ServiceMutex = @"Local\GameSwitcherWatcher";
        public const string ServiceExitEvent = @"Local\GameSwitcherWatcherExit";
        static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "GameSwitcher.lnk");

        static int Schtasks(string args)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        public static bool IsEnabled() => Schtasks($"/Query /TN \"{TaskName}\"") == 0;

        public static void Enable(bool startNow)
        {
            string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Applies the GameSwitcher actions while a selected game is running.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author""><Exec>
    <Command>{SecurityElement.Escape(Paths.Exe)}</Command>
    <Arguments>--watcher</Arguments>
    <WorkingDirectory>{SecurityElement.Escape(Paths.InstallDir)}</WorkingDirectory>
  </Exec></Actions>
</Task>";
            string file = Path.Combine(Path.GetTempPath(), "GameSwitcherTask.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            try
            {
                if (Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F") != 0) throw new Exception("schtasks /Create failed.");
            }
            finally { File.Delete(file); }
            CreateStartMenuEntry();
            if (startNow) StartService();
        }

        public static void Disable() => Schtasks($"/Delete /TN \"{TaskName}\" /F");

        public static void CreateStartMenuEntry()
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic link = shell.CreateShortcut(StartMenuLink);
            link.TargetPath = Paths.Exe;
            link.WorkingDirectory = Paths.InstallDir;
            link.IconLocation = Paths.Exe + ",0";
            link.Description = "GameSwitcher";
            link.Save();
        }

        public static bool ServiceRunning()
        {
            if (Mutex.TryOpenExisting(ServiceMutex, out Mutex m)) { m.Dispose(); return true; }
            return false;
        }

        public static void StartService()
        {
            if (!ServiceRunning()) Process.Start(new ProcessStartInfo(Paths.Exe, "--watcher") { UseShellExecute = true, WorkingDirectory = Paths.InstallDir });
        }

        // Asks the service to revert its changes and exit; waits up to 15 seconds
        public static void StopService()
        {
            if (!ServiceRunning()) return;
            if (EventWaitHandle.TryOpenExisting(ServiceExitEvent, out EventWaitHandle exit)) using (exit) exit.Set();
            for (int i = 0; i < 150 && ServiceRunning(); i++) Thread.Sleep(100);
        }

        // Used by the uninstaller (GameSwitcher.exe --uninstall)
        public static void Uninstall()
        {
            StopService();
            Disable();
            try { File.Delete(StartMenuLink); } catch { }
        }
    }
}
