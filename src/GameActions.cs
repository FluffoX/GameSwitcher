using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using GameSwitcher.Native;
using static GameSwitcher.Strings;

namespace GameSwitcher
{
    // What was changed on game start, so it can be reverted afterwards. Written to state.json after
    // every step: if the PC or the service crashes during a game, the next start reverts it.
    class ChangeState
    {
        public string GameKey { get; set; }
        public string GameName { get; set; }
        public bool Monitors { get; set; }
        public DisplayChange Display { get; set; }
        public List<string> HdrDisplays { get; set; } = new List<string>();
        public List<SdrChange> Sdr { get; set; } = new List<SdrChange>();
        public bool NightLight { get; set; }
        public DeviceChange Audio { get; set; }
        public DeviceChange Mic { get; set; }
        public VolumeChange Volume { get; set; }
        public PowerChange Power { get; set; }
        public bool KeepAwake { get; set; }
        public List<string> ClosedApps { get; set; } = new List<string>();
        public List<string> StartedApps { get; set; } = new List<string>();
        public string ScriptEnd { get; set; }
    }
    class DisplayChange { public int Width { get; set; } public int Height { get; set; } public int Hz { get; set; } public int SetWidth { get; set; } public int SetHeight { get; set; } public int SetHz { get; set; } }
    class SdrChange { public string Key { get; set; } public int Level { get; set; } }
    class DeviceChange { public string PreviousId { get; set; } public string PreviousName { get; set; } public string SetId { get; set; } }
    class VolumeChange { public int Previous { get; set; } public int Set { get; set; } }
    class PowerChange { public string PreviousId { get; set; } public string PreviousName { get; set; } }

    // Actions on game start and exit
    static class GameActions
    {
        public static ChangeState Changes;
        static string StatePath => Paths.Data("state.json");

        static void SaveState() { if (Changes != null) Json.WriteFile(StatePath, Changes); }
        public static ChangeState LoadPending() => Json.ReadFile<ChangeState>(StatePath);
        static void ClearState() { try { File.Delete(StatePath); } catch { } }

        // Runs one action; an error is logged but never stops the other actions
        static void Step(string name, Action body)
        {
            try { body(); }
            catch (Exception ex) { Log.Write(T("log.actionError", name, ex.Message)); }
            SaveState();
        }

        static Process[] AppProcesses(string path) => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path));

        // Closes a program gracefully (closes its window), forcibly if needed
        static void StopApp(Process p)
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow() && p.WaitForExit(5000)) return;
                if (!p.HasExited) p.Kill();
            }
            catch { if (!p.HasExited) throw; }
        }

        static void StartApp(string path) =>
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) });

        // Runs a user command hidden; scripts and programs can be given as a plain path.
        // The game name is passed in the environment variable GAMESWITCHER_GAME.
        static void RunCommand(string command, string gameName)
        {
            command = (command ?? "").Trim();
            if (command.Length == 0) return;
            Environment.SetEnvironmentVariable("GAMESWITCHER_GAME", gameName);
            string file = command.Trim('"');
            if (File.Exists(file) && file.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{file}\"") { UseShellExecute = false, CreateNoWindow = true });
            else if (File.Exists(file))
                StartApp(file);
            else
                Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/c " + command) { UseShellExecute = false, CreateNoWindow = true });
        }

        public static void Start(Config config, string gameKey, string gameName)
        {
            ActionSet cfg = config.ActionsFor(gameKey);
            var c = Changes = new ChangeState { GameKey = gameKey, GameName = gameName };
            SaveState();

            if (cfg.On("Monitors"))
                Step(T("act.monitors"), () =>
                {
                    if (DisplayConfig.CountActiveDisplays() > 1 && DisplayConfig.DetachSecondary())
                    {
                        c.Monitors = true;
                        Log.Write(T("log.monitorsOff"));
                        Thread.Sleep(1500);
                    }
                });

            var display = cfg["Display"];
            if (cfg.On("Display") && display.Int("Width") > 0)
                Step(T("act.display"), () =>
                {
                    DisplayMode cur = DisplayModes.Current();
                    int w = display.Int("Width"), h = display.Int("Height"), hz = display.Int("Hz");
                    if (cur.Width == w && cur.Height == h && cur.Hz == hz) return;
                    DisplayModes.Set(w, h, hz);
                    DisplayMode set = DisplayModes.Current();
                    c.Display = new DisplayChange { Width = cur.Width, Height = cur.Height, Hz = cur.Hz, SetWidth = set.Width, SetHeight = set.Height, SetHz = set.Hz };
                    Log.Write(T("log.displaySet", set));
                });

            if (cfg.On("Hdr"))
                Step("HDR", () =>
                {
                    foreach (var d in DisplayConfig.GetDisplays().Where(x => x.HdrSupported && !x.HdrEnabled))
                    {
                        DisplayConfig.SetHdr(d, true);
                        c.HdrDisplays.Add(d.Key);
                        Log.Write(T("log.hdrOn", d.Name));
                    }
                    if (c.HdrDisplays.Count > 0) Thread.Sleep(1000);   // let Windows finish the switch
                });

            if (cfg.On("Sdr"))
                Step(T("act.sdr"), () =>
                {
                    int level = cfg["Sdr"].Int("Level", 30);
                    foreach (var d in DisplayConfig.GetDisplays().Where(x => x.HdrEnabled))
                    {
                        int prev = DisplayConfig.GetSdrBrightness(d);
                        if (prev == level) continue;
                        DisplayConfig.SetSdrBrightness(d, level);
                        c.Sdr.Add(new SdrChange { Key = d.Key, Level = prev });
                        Log.Write(T("log.sdrSet", level, d.Name));
                    }
                });

            if (cfg.On("NightLight"))
                Step(T("act.nightLight"), () =>
                {
                    bool? state = NightLight.Get();
                    if (state == null) Log.Write(T("log.unsupported", T("act.nightLight")));
                    else if (state == true) { NightLight.Set(false); c.NightLight = true; Log.Write(T("log.nightLightOff")); }
                });

            foreach (string kind in new[] { "Audio", "Mic" })
            {
                var a = cfg[kind];
                string id = a.Str("DeviceId");
                if (!cfg.On(kind) || id.Length == 0) continue;
                int flow = kind == "Mic" ? Audio.Input : Audio.Output;
                Step(kind, () =>
                {
                    string previous = Audio.GetDefaultId(flow);
                    if (previous == id) return;
                    if (!Audio.IsConnected(flow, id)) throw new Exception(T("log.audioMissing", a.Str("DeviceName")));
                    string previousName = Audio.GetDefaultName(flow);
                    Audio.SetDefault(id, kind == "Mic");
                    var change = new DeviceChange { PreviousId = previous, PreviousName = previousName, SetId = id };
                    if (kind == "Mic") c.Mic = change; else c.Audio = change;
                    Log.Write(T(kind == "Mic" ? "log.micSwitched" : "log.audioSwitched", a.Str("DeviceName")));
                });
            }

            if (cfg.On("Volume"))
                Step(T("act.volume"), () =>
                {
                    int level = cfg["Volume"].Int("Level", 50), prev = Audio.GetVolume();
                    if (prev < 0 || prev == level) return;
                    Audio.SetVolume(level);
                    c.Volume = new VolumeChange { Previous = prev, Set = level };
                    Log.Write(T("log.volumeSet", level));
                });

            string planId = cfg["Power"].Str("PlanId");
            if (cfg.On("Power") && planId.Length > 0)
                Step(T("act.power"), () =>
                {
                    PowerPlan active = PowerPlans.Active();
                    if (active == null || active.Id == planId) return;
                    PowerPlans.Activate(planId);
                    c.Power = new PowerChange { PreviousId = active.Id, PreviousName = active.Name };
                    Log.Write(T("log.powerSet", cfg["Power"].Str("PlanName")));
                });

            if (cfg.On("KeepAwake"))
                Step(T("act.keepAwake"), () =>
                {
                    KeepAwake.Set(true);
                    c.KeepAwake = true;
                    Log.Write(T("log.keepAwake"));
                });

            if (cfg.On("CloseApps"))
            {
                foreach (string app in cfg["CloseApps"].Strings("Apps"))
                {
                    foreach (Process p in AppProcesses(app))
                    {
                        string path = null;
                        try { path = p.MainModule.FileName; } catch { }
                        try
                        {
                            StopApp(p);
                            path = path ?? app;
                            if (!c.ClosedApps.Contains(path, StringComparer.OrdinalIgnoreCase))
                            {
                                c.ClosedApps.Add(path);
                                Log.Write(T("log.closed", Path.GetFileName(path)));
                            }
                        }
                        catch (Exception ex) { Log.Write(T("log.closeError", p.ProcessName, ex.Message)); }
                    }
                }
                SaveState();
            }

            if (cfg.On("StartApps"))
            {
                foreach (string app in cfg["StartApps"].Strings("Apps"))
                {
                    if (AppProcesses(app).Length > 0) continue;   // already running, so don't stop it afterwards either
                    try
                    {
                        StartApp(app);
                        c.StartedApps.Add(app);
                        Log.Write(T("log.started", Path.GetFileName(app)));
                    }
                    catch (Exception ex) { Log.Write(T("log.startError", app, ex.Message)); }
                }
                SaveState();
            }

            if (cfg.On("Script"))
                Step(T("act.script"), () =>
                {
                    string start = cfg["Script"].Str("Start");
                    if (start.Length > 0) { RunCommand(start, gameName); Log.Write(T("log.scriptRun", start)); }
                    c.ScriptEnd = cfg["Script"].Str("End");
                });
        }

        // Reverts what Start changed (or what a crashed session left behind, see LoadPending).
        // Only what is still as the service left it is reverted: manual changes during the game win.
        public static void End(Config config)
        {
            ChangeState c = Changes;
            if (c == null) { ClearState(); return; }
            ActionSet cfg = config.ActionsFor(c.GameKey);

            if (!string.IsNullOrEmpty(c.ScriptEnd))
            {
                try { RunCommand(c.ScriptEnd, c.GameName); Log.Write(T("log.scriptRun", c.ScriptEnd)); }
                catch (Exception ex) { Log.Write(T("log.actionError", T("act.script"), ex.Message)); }
            }

            if (c.StartedApps.Count > 0)
            {
                if (cfg.Revert("StartApps"))
                    foreach (string app in c.StartedApps)
                    {
                        foreach (Process p in AppProcesses(app))
                        {
                            try { StopApp(p); } catch (Exception ex) { Log.Write(T("log.closeError", p.ProcessName, ex.Message)); }
                        }
                        Log.Write(T("log.closedAgain", Path.GetFileName(app)));
                    }
                else Log.Write(T("log.startedKept"));
            }

            if (c.ClosedApps.Count > 0)
            {
                if (cfg.Revert("CloseApps"))
                    foreach (string app in c.ClosedApps)
                    {
                        if (AppProcesses(app).Length > 0) continue;
                        try { StartApp(app); Log.Write(T("log.restarted", Path.GetFileName(app))); }
                        catch (Exception ex) { Log.Write(T("log.startError", app, ex.Message)); }
                    }
                else Log.Write(T("log.closedKept"));
            }

            if (c.KeepAwake) KeepAwake.Set(false);

            if (c.Power != null && cfg.Revert("Power"))
            {
                try
                {
                    if (PowerPlans.Active()?.Id == cfg["Power"].Str("PlanId"))
                    {
                        PowerPlans.Activate(c.Power.PreviousId);
                        Log.Write(T("log.powerSet", c.Power.PreviousName));
                    }
                }
                catch (Exception ex) { Log.Write(T("log.actionError", T("act.power"), ex.Message)); }
            }

            if (c.Volume != null && cfg.Revert("Volume"))
            {
                try
                {
                    if (Audio.GetVolume() == c.Volume.Set)
                    {
                        Audio.SetVolume(c.Volume.Previous);
                        Log.Write(T("log.volumeSet", c.Volume.Previous));
                    }
                }
                catch (Exception ex) { Log.Write(T("log.actionError", T("act.volume"), ex.Message)); }
            }

            foreach (string kind in new[] { "Mic", "Audio" })
            {
                DeviceChange saved = kind == "Mic" ? c.Mic : c.Audio;
                if (saved == null) continue;
                if (!cfg.Revert(kind)) { Log.Write(T("log.audioKept")); continue; }
                int flow = kind == "Mic" ? Audio.Input : Audio.Output;
                try
                {
                    if (Audio.GetDefaultId(flow) == saved.SetId && Audio.IsConnected(flow, saved.PreviousId))
                    {
                        Audio.SetDefault(saved.PreviousId, kind == "Mic");
                        Log.Write(T(kind == "Mic" ? "log.micSwitched" : "log.audioSwitched", saved.PreviousName));
                    }
                }
                catch (Exception ex) { Log.Write(T("log.audioError", ex.Message)); }
            }

            if (c.NightLight && cfg.Revert("NightLight"))
            {
                try { NightLight.Set(true); Log.Write(T("log.nightLightOn")); }
                catch (Exception ex) { Log.Write(T("log.actionError", T("act.nightLight"), ex.Message)); }
            }

            if (c.Sdr.Count > 0 && cfg.Revert("Sdr"))
            {
                try
                {
                    foreach (var d in DisplayConfig.GetDisplays().Where(x => x.HdrEnabled))
                    {
                        SdrChange saved = c.Sdr.FirstOrDefault(s => s.Key == d.Key);
                        if (saved == null || saved.Level < 0) continue;
                        DisplayConfig.SetSdrBrightness(d, saved.Level);
                        Log.Write(T("log.sdrSet", saved.Level, d.Name));
                    }
                }
                catch (Exception ex) { Log.Write(T("log.actionError", T("act.sdr"), ex.Message)); }
            }

            if (c.HdrDisplays.Count > 0)
            {
                if (cfg.Revert("Hdr"))
                {
                    try
                    {
                        foreach (var d in DisplayConfig.GetDisplays().Where(x => c.HdrDisplays.Contains(x.Key) && x.HdrEnabled))
                        {
                            DisplayConfig.SetHdr(d, false);
                            Log.Write(T("log.hdrOff", d.Name));
                        }
                    }
                    catch (Exception ex) { Log.Write(T("log.hdrError", ex.Message)); }
                }
                else Log.Write(T("log.hdrKept"));
            }

            if (c.Display != null && cfg.Revert("Display"))
            {
                try
                {
                    DisplayMode cur = DisplayModes.Current();
                    if (cur.Width == c.Display.SetWidth && cur.Height == c.Display.SetHeight && cur.Hz == c.Display.SetHz)
                    {
                        DisplayModes.Set(c.Display.Width, c.Display.Height, c.Display.Hz);
                        Log.Write(T("log.displaySet", DisplayModes.Current()));
                    }
                }
                catch (Exception ex) { Log.Write(T("log.actionError", T("act.display"), ex.Message)); }
            }

            if (c.Monitors && cfg.Revert("Monitors"))
            {
                try { DisplayConfig.RestoreExtended(); Log.Write(T("log.monitorsOn")); }
                catch (Exception ex) { Log.Write(T("log.actionError", T("act.monitors"), ex.Message)); }
            }

            Changes = null;
            ClearState();
        }
    }
}
