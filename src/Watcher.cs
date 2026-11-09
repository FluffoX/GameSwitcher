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
            using (var mutex = new Mutex(true, Autostart.ServiceMutex, out bool createdNew))
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

            CreateTrayIcons();
            tray.Icon = iconNormal;
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
            // Optionally a left click toggles HDR; then the window opens via the menu only,
            // because a double click would toggle twice
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left && config.TrayHdrToggle) TrayToggleHdr(); };
            tray.DoubleClick += (s, e) => { if (!config.TrayHdrToggle) OpenGui(); };

            UpdateConfig();
            Log.Write(T("log.watcherStarted"));
            RecoverPendingState();

            tray.Visible = true;
            ListenForExitRequest();
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
            UpdateTrayTexts(Pause.Get());
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

        void SetPause(int hours, bool untilReboot = false, bool resume = false)
        {
            if (resume) { Pause.Clear(); Log.Write(T("log.resumed")); }
            else { Pause.Set(hours, untilReboot); Log.Write(T("log.paused")); }
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
                var paused = Pause.Get();
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
                miStatus.Text = Pause.Describe(paused);
            else if (active != null)
                miStatus.Text = T("tray.active", active == ManualGame ? T("tray.manual") : active.Name);
            else
                miStatus.Text = T("tray.idle");

            bool? hdr = UpdateTrayIcon();
            string tip = "GameSwitcher – " + (hdr == null ? "" : (hdr.Value ? T("hdr.on") : T("hdr.off")) + " – ") + miStatus.Text;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 60) + "..." : tip;
        }

        #endregion

        #region HDR in the tray icon

        Icon iconNormal, iconHdrOn, iconHdrOff;
        DateTime lastTrayToggle = DateTime.MinValue;

        // Colored icon with a green dot = HDR on, gray icon = HDR off
        void CreateTrayIcons()
        {
            string iconPath = Path.Combine(Paths.AppDir, "icon.ico");
            if (!File.Exists(iconPath)) { iconNormal = iconHdrOn = iconHdrOff = SystemIcons.Application; return; }
            iconNormal = new Icon(iconPath, SystemInformation.SmallIconSize);
            using (var source = new Icon(iconPath, 48, 48).ToBitmap())
            {
                iconHdrOn = DrawTrayIcon(source, gray: false, dot: true);
                iconHdrOff = DrawTrayIcon(source, gray: true, dot: false);
            }
        }

        static Icon DrawTrayIcon(Bitmap source, bool gray, bool dot)
        {
            int n = SystemInformation.SmallIconSize.Width;
            using (var bmp = new Bitmap(n, n, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var attr = new System.Drawing.Imaging.ImageAttributes())
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    if (gray)
                        attr.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(new[]
                        {
                            new[] { .3f, .3f, .3f, 0f, 0f }, new[] { .59f, .59f, .59f, 0f, 0f }, new[] { .11f, .11f, .11f, 0f, 0f },
                            new[] { 0f, 0f, 0f, .8f, 0f }, new[] { 0f, 0f, 0f, 0f, 1f }
                        }));
                    g.DrawImage(source, new Rectangle(0, 0, n, n), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attr);
                    if (dot)
                    {
                        float d = n * 0.48f, x = n - d, y = n - d;
                        using (var outline = new SolidBrush(Color.FromArgb(255, 20, 20, 20))) g.FillEllipse(outline, x - 1, y - 1, d + 1, d + 1);
                        using (var green = new SolidBrush(Color.FromArgb(255, 40, 210, 90))) g.FillEllipse(green, x, y, d - 1, d - 1);
                    }
                }
                IntPtr h = bmp.GetHicon();
                return (Icon)Icon.FromHandle(h).Clone();
            }
        }

        // Shows the HDR state in the icon while the option is on; returns the state (null if not shown)
        bool? UpdateTrayIcon()
        {
            bool? hdr = null;
            if (config.TrayHdrToggle)
            {
                try
                {
                    var displays = DisplayConfig.GetDisplays().Where(d => d.HdrSupported).ToList();
                    if (displays.Count > 0) hdr = displays.Any(d => d.HdrEnabled);
                }
                catch { }
            }
            Icon wanted = hdr == null ? iconNormal : hdr.Value ? iconHdrOn : iconHdrOff;
            if (tray.Icon != wanted) tray.Icon = wanted;
            return hdr;
        }

        void TrayToggleHdr()
        {
            // ignore a second click while Windows is still switching (e.g. from a double click)
            if ((DateTime.Now - lastTrayToggle).TotalSeconds < 1.5) return;
            lastTrayToggle = DateTime.Now;
            ToggleHdr();
            UpdateTrayTexts(Pause.Get());
        }

        #endregion

        #region Tray actions

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

        // The window ("Stop service") and the uninstaller signal this event: revert and exit cleanly
        readonly EventWaitHandle exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, Autostart.ServiceExitEvent);
        readonly Control invoker = new Control();

        void ListenForExitRequest()
        {
            invoker.CreateControl();
            ThreadPool.RegisterWaitForSingleObject(exitRequest, (s, timedOut) => invoker.BeginInvoke((Action)ExitService), null, -1, true);
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
                exitRequest.Dispose();
                invoker.Dispose();
            }
            base.Dispose(disposing);
        }

        #endregion
    }
}
