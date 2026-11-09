using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Windows.Forms;
using GameSwitcher.Native;
using Microsoft.Win32;
using static GameSwitcher.Strings;

namespace GameSwitcher
{
    // Background service with a tray icon. Detects running games (the selected ones, optionally every game
    // Windows knows, or the manual game mode via hotkey), applies the actions of the matching profile,
    // reverts them when the game exits and records play time.
    static class Watcher
    {
        public static int Run()
        {
            using (var mutex = new Mutex(true, @"Local\GameSwitcherWatcher", out bool createdNew))
            {
                if (!createdNew) return 0;   // already running
                Log.Trim();
                using (var context = new WatcherContext()) Application.Run(context);
            }
            return 0;
        }
    }

    class WatcherContext : ApplicationContext
    {
        const int IdleSecondsBeforeRevert = 10;   // covers launchers that restart the game process

        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem miStatus, miOpen, miHdr, miManual, miPause, miPause1, miPause2, miPauseReboot, miResume, miExit;
        readonly HotkeyWindow hotkey = new HotkeyWindow();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly DateTime bootTime = GetBootTime();

        Config config = new Config();
        DateTime? configTime;
        string hotkeyText;
        Dictionary<string, GameRef> exeMap = new Dictionary<string, GameRef>();       // process name (lower case) -> game
        Dictionary<string, GameRef> libraryMap = new Dictionary<string, GameRef>();   // same for every game in the GUI's list
        Dictionary<string, GameRef> autoMap = new Dictionary<string, GameRef>();      // games Windows knows (GameConfigStore)
        DateTime autoMapTime = DateTime.MinValue;
        bool manual;
        GameRef active;
        int idleSeconds;
        readonly Dictionary<string, (string Name, DateTime Last)> play = new Dictionary<string, (string, DateTime)>();
        DateTime playFlush = DateTime.Now;

        static readonly GameRef ManualGame = new GameRef { Key = "manual" };

        public WatcherContext()
        {
            hotkey.Pressed += (s, e) => { manual = !manual; Tick(); };

            string iconPath = Path.Combine(Paths.AppDir, "icon.ico");
            tray.Icon = File.Exists(iconPath) ? new Icon(iconPath, SystemInformation.SmallIconSize) : SystemIcons.Application;
            miStatus = Add(menu.Items, null); miStatus.Enabled = false;
            menu.Items.Add(new ToolStripSeparator());
            miOpen = Add(menu.Items, OpenGui); miOpen.Font = new Font(miOpen.Font, FontStyle.Bold);
            miHdr = Add(menu.Items, ToggleHdr);
            miManual = Add(menu.Items, () => { manual = !manual; Tick(); });
            miPause = Add(menu.Items, null);
            miPause1 = Add(miPause.DropDownItems, () => SetPause(1));
            miPause2 = Add(miPause.DropDownItems, () => SetPause(2));
            miPauseReboot = Add(miPause.DropDownItems, () => SetPause(0, untilReboot: true));
            miPause.DropDownItems.Add(new ToolStripSeparator());
            miResume = Add(miPause.DropDownItems, () => SetPause(0, resume: true));
            menu.Items.Add(new ToolStripSeparator());
            miExit = Add(menu.Items, ExitService);
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => OpenGui();

            UpdateConfig();
            Log.Write(T("log.watcherStarted"));
            RecoverPendingState();

            tray.Visible = true;
            timer.Tick += (s, e) => Tick();
            timer.Start();
            Tick();
        }

        static ToolStripMenuItem Add(ToolStripItemCollection items, Action onClick)
        {
            var item = new ToolStripMenuItem();
            if (onClick != null) item.Click += (s, e) => onClick();
            items.Add(item);
            return item;
        }

        #region Configuration

        void UpdateConfig()
        {
            string path = Paths.Data("settings.json");
            DateTime? time = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
            if (configTime.HasValue && time == configTime) return;
            configTime = time;
            config = Config.Load();
            Strings.Load(config.Raw);

            exeMap = MapByExe(config.Selected);
            libraryMap = MapByExe(config.Library);
            autoMapTime = DateTime.MinValue;

            if (config.Hotkey != hotkeyText)
            {
                hotkeyText = config.Hotkey;
                hotkey.Unregister();
                if (hotkeyText.Length > 0 && !hotkey.Register(hotkeyText)) Log.Write(T("log.hotkeyFailed", hotkeyText));
            }
            UpdateTrayTexts(GetPause());
        }

        static Dictionary<string, GameRef> MapByExe(IEnumerable<GameRef> games)
        {
            var map = new Dictionary<string, GameRef>();
            foreach (var g in games)
                foreach (string exe in g.Exes) map[Path.GetFileNameWithoutExtension(exe).ToLowerInvariant()] = g;
            return map;
        }

        // Games Windows has seen running (Game Bar / Game Mode know them as games)
        void UpdateAutoMap()
        {
            if (!config.AutoDetect) { autoMap = new Dictionary<string, GameRef>(); return; }
            if ((DateTime.Now - autoMapTime).TotalSeconds < 60) return;
            autoMapTime = DateTime.Now;
            var map = new Dictionary<string, GameRef>();
            using (var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children"))
            {
                foreach (string sub in children?.GetSubKeyNames() ?? new string[0])
                {
                    using (var k = children.OpenSubKey(sub))
                    {
                        if (!(k?.GetValue("MatchedExeFullPath") is string path) || path.Length == 0) continue;
                        string exe = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                        // a game from the GUI's list keeps its key (profile, play time); others get the key the GUI uses
                        if (libraryMap.TryGetValue(exe, out GameRef known)) { map[exe] = known; continue; }
                        string name = null;
                        try { name = FileVersionInfo.GetVersionInfo(path).ProductName?.Trim(); } catch { }
                        map[exe] = new GameRef { Key = "auto:" + path.ToLowerInvariant(), Name = string.IsNullOrEmpty(name) ? exe : name, Exes = new[] { exe } };
                    }
                }
            }
            autoMap = map;
        }

        #endregion

        #region Pause

        static DateTime GetBootTime()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem"))
                    foreach (ManagementObject os in searcher.Get())
                        return ManagementDateTimeConverter.ToDateTime((string)os["LastBootUpTime"]);
            }
            catch { }
            return DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount);
        }

        static DateTime? ParseDate(string s) =>
            DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime d) ? d.ToLocalTime() : (DateTime?)null;

        // pause.json is shared with the GUI: { "Until": "<ISO time>" } or { "UntilReboot": true, "BootTime": "<ISO time>" }
        Dictionary<string, object> GetPause()
        {
            string path = Paths.Data("pause.json");
            var p = Json.ReadFile(path);
            if (p == null) return null;
            if (p.Bool("UntilReboot") && ParseDate(p.Str("BootTime")) is DateTime boot && Math.Abs((boot - bootTime).TotalSeconds) < 5) return p;
            if (ParseDate(p.Str("Until")) is DateTime until && until > DateTime.Now) return p;
            try { File.Delete(path); } catch { }   // expired
            return null;
        }

        void SetPause(int hours, bool untilReboot = false, bool resume = false)
        {
            string path = Paths.Data("pause.json");
            if (resume)
            {
                try { File.Delete(path); } catch { }
                Log.Write(T("log.resumed"));
            }
            else
            {
                var p = untilReboot
                    ? new Dictionary<string, object> { ["UntilReboot"] = true, ["BootTime"] = bootTime.ToString("o") }
                    : new Dictionary<string, object> { ["Until"] = DateTime.Now.AddHours(hours).ToString("o") };
                Json.WriteFile(path, p);
                Log.Write(T("log.paused"));
            }
            Tick();
        }

        #endregion

        #region Play time

        void AddPlaytime(string key, string name, double seconds)
        {
            string path = Paths.Data("playtime.json");
            var data = Json.ReadFile(path) ?? new Dictionary<string, object>();
            var old = data.Dict(key);
            data[key] = new Dictionary<string, object>
            {
                ["Name"] = name,
                ["Seconds"] = (long)Math.Round(old.Double("Seconds") + seconds),
                ["LastPlayed"] = DateTime.Now.ToString("o")
            };
            try { Json.WriteFile(path, data); } catch { }
        }

        void UpdatePlaytime(List<GameRef> running)
        {
            DateTime now = DateTime.Now;
            foreach (var g in running)
                if (!play.ContainsKey(g.Key)) play[g.Key] = (g.Name, now);
            bool flush = (now - playFlush).TotalSeconds >= 60;
            foreach (string key in play.Keys.ToList())
            {
                var (name, last) = play[key];
                if (!running.Any(g => g.Key == key))
                {
                    AddPlaytime(key, name, (now - last).TotalSeconds);
                    play.Remove(key);
                }
                else if (flush)
                {
                    AddPlaytime(key, name, (now - last).TotalSeconds);
                    play[key] = (name, now);
                }
            }
            if (flush) playFlush = now;
        }

        #endregion

        #region Detection

        List<GameRef> GetRunningGames()
        {
            var names = new HashSet<string>();
            foreach (Process p in Process.GetProcesses())
            {
                names.Add(p.ProcessName.ToLowerInvariant());
                p.Dispose();
            }
            var found = new List<GameRef>();
            foreach (var map in new[] { exeMap, autoMap })
                foreach (var kv in map)
                    if (names.Contains(kv.Key) && !found.Any(g => g.Key == kv.Value.Key)) found.Add(kv.Value);
            return found;
        }

        void Notify(string text)
        {
            if (config.Notifications) tray.ShowBalloonTip(4000, "GameSwitcher", text, ToolTipIcon.Info);
        }

        void StartSession(GameRef game)
        {
            active = game;
            idleSeconds = 0;
            string name = game == ManualGame ? T("tray.manual") : game.Name;
            Log.Write(game == ManualGame ? T("log.manualStart") : T("log.gameStarted", name));
            GameActions.Start(config, game.Key, name);
            Notify(T("notify.start", name));
        }

        void StopSession()
        {
            Log.Write(active.Key == "manual" ? T("log.manualStop") : T("log.noGame"));
            GameActions.End(config);
            active = null;
            Notify(T("notify.end"));
        }

        void Tick()
        {
            try
            {
                UpdateConfig();
                UpdateAutoMap();
                List<GameRef> running = GetRunningGames();
                UpdatePlaytime(running);
                if (manual) running.Insert(0, ManualGame);
                var paused = GetPause();
                if (paused != null) running.Clear();

                if (running.Count > 0 && active == null) StartSession(running[0]);
                else if (running.Count > 0) idleSeconds = 0;
                else if (active != null)
                {
                    idleSeconds++;
                    // manual mode and pause end at once; a game gets a grace period for launcher restarts
                    if (active.Key == "manual" || paused != null || idleSeconds >= IdleSecondsBeforeRevert) StopSession();
                }
                UpdateTrayTexts(paused);
            }
            catch (Exception ex)
            {
                Log.Write(T("log.error", ex.Message));
            }
        }

        // A state file left by a crash: adopt it if that game is still running, otherwise revert it now
        void RecoverPendingState()
        {
            ChangeState pending = GameActions.LoadPending();
            if (pending == null) return;
            UpdateAutoMap();
            GameActions.Changes = pending;
            GameRef stillRunning = GetRunningGames().FirstOrDefault(g => g.Key == pending.GameKey);
            if (stillRunning != null)
            {
                active = stillRunning;
                if (pending.KeepAwake) KeepAwake.Set(true);
            }
            else
            {
                Log.Write(T("log.recovered"));
                GameActions.End(config);
            }
        }

        #endregion

        #region Tray

        void UpdateTrayTexts(Dictionary<string, object> paused)
        {
            miOpen.Text = T("tray.open");
            miHdr.Text = T("btn.hdrToggle");
            miManual.Text = (manual ? T("tray.manualStop") : T("tray.manualStart")) + (hotkeyText?.Length > 0 ? $"   ({hotkeyText})" : "");
            miPause.Text = T("btn.pause");
            miPause1.Text = T("pause.1h");
            miPause2.Text = T("pause.2h");
            miPauseReboot.Text = T("pause.reboot");
            miResume.Text = T("btn.resume");
            miResume.Enabled = paused != null;
            miExit.Text = T("tray.exit");

            if (paused != null)
                miStatus.Text = paused.Bool("UntilReboot") ? T("pause.untilReboot") : T("pause.until", ParseDate(paused.Str("Until"))?.ToString("t"));
            else if (active != null)
                miStatus.Text = T("tray.active", active == ManualGame ? T("tray.manual") : active.Name);
            else
                miStatus.Text = T("tray.idle");

            string tip = "GameSwitcher – " + miStatus.Text;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 60) + "..." : tip;
        }

        void OpenGui()
        {
            try { Process.Start(new ProcessStartInfo(Paths.Exe) { UseShellExecute = true, WorkingDirectory = Paths.InstallDir }); }
            catch (Exception ex) { Log.Write(T("log.error", ex.Message)); }
        }

        void ToggleHdr()
        {
            try
            {
                var displays = DisplayConfig.GetDisplays().Where(d => d.HdrSupported).ToList();
                bool enable = !displays.Any(d => d.HdrEnabled);
                foreach (var d in displays.Where(d => d.HdrEnabled != enable)) DisplayConfig.SetHdr(d, enable);
            }
            catch (Exception ex) { Log.Write(T("log.hdrError", ex.Message)); }
        }

        void ExitService()
        {
            timer.Stop();
            if (active != null) StopSession();
            tray.Visible = false;
            hotkey.Unregister();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                tray.Dispose();
                menu.Dispose();
                hotkey.Dispose();
            }
            base.Dispose(disposing);
        }

        #endregion
    }
}
