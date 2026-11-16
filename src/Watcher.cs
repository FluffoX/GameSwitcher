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
        readonly ToolStripMenuItem miStatus, miOpen, miRecent, miHdr, miManual, miPause, miPause1, miPause2, miPauseReboot, miResume, miExit;
        readonly List<ToolStripItem> batteryItems = new List<ToolStripItem>();
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
        DateTime playFlush = DateTime.Now, sessionStart;

        static readonly GameRef ManualGame = new GameRef { Key = "manual" };

        public WatcherContext()
        {
            hotkey.Pressed += (s, e) => { manual = !manual; Tick(); };

            CreateTrayIcons();
            tray.Icon = iconNormal;
            miStatus = Add(menu.Items, null); miStatus.Enabled = false;
            menu.Items.Add(new ToolStripSeparator());
            miOpen = Add(menu.Items, OpenGui); miOpen.Font = new Font(miOpen.Font, FontStyle.Bold);
            miRecent = Add(menu.Items, null);
            miRecent.DropDownItems.Add(new ToolStripMenuItem("-"));   // filled when the menu opens
            menu.Opening += (s, e) => UpdateMenuExtras();
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
            RecoverCouchState();
            StartDevicePolling();
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
            // per day, for the statistics
            var days = old.Dict("Days") ?? new Dictionary<string, object>();
            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            days[today] = (long)Math.Round(days.Double(today) + seconds);
            data[key] = new Dictionary<string, object>
            {
                ["Name"] = name,
                ["Seconds"] = (long)Math.Round(old.Double("Seconds") + seconds),
                ["LastPlayed"] = DateTime.Now.ToString("o"),
                ["Days"] = days
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

        // Shows a notification; the parts are joined, empty ones skipped. General notifications
        // (settings applied/restored) depend on their option, the extras on their own options.
        void Notify(params string[] parts)
        {
            string text = string.Join("\n", parts.Where(p => !string.IsNullOrEmpty(p)));
            if (text.Length > 0) tray.ShowBalloonTip(5000, "GameSwitcher", text, ToolTipIcon.Info);
        }

        static string FormatDuration(TimeSpan t)
        {
            long minutes = Math.Max(1, (long)Math.Round(t.TotalMinutes));
            return minutes >= 60 ? T("time.hm", minutes / 60, minutes % 60) : T("time.m", minutes);
        }

        void StartSession(GameRef game)
        {
            active = game;
            idleSeconds = 0;
            sessionStart = DateTime.Now;
            string name = game == ManualGame ? T("tray.manual") : game.Name;
            Log.Write(game == ManualGame ? T("log.manualStart") : T("log.gameStarted", name));
            if (game != ManualGame) RecordGraphicsPrograms(game);
            GameActions.Start(config, game.Key, name, couch: couch != null && config.Couch.SkipProfile);
            Notify(config.Notifications ? T("notify.start", name) : null, config.BatteryGameStart ? LowBatteryText("notify.batteryGameStart") : null);
        }

        void StopSession()
        {
            bool game = active.Key != "manual";
            Log.Write(game ? T("log.noGame") : T("log.manualStop"));
            GameActions.End(config);
            string summary = game && config.SessionSummary ? T("notify.session", active.Name, FormatDuration(DateTime.Now - sessionStart)) : null;
            active = null;
            Notify(summary, config.Notifications ? T("notify.end") : null);
        }

        // Per-program graphics settings (GPU, Auto HDR) need the program paths; Windows applies them from the next start
        void RecordGraphicsPrograms(GameRef game)
        {
            try
            {
                ActionSet a = config.ActionsFor(game.Key);
                string hdr = a["AutoHdr"].Str("Mode");
                if (!a.On("Gpu") && hdr != "on" && hdr != "off") return;
                var names = new HashSet<string>(game.Exes.Select(e => Path.GetFileNameWithoutExtension(e).ToLowerInvariant()));
                var paths = new List<string>();
                foreach (Process p in Process.GetProcesses())
                {
                    if (names.Contains(p.ProcessName.ToLowerInvariant())) paths.Add(GraphicsSync.ProcessPath(p));
                    p.Dispose();
                }
                GraphicsSync.GameStarted(config, game.Key, paths);
            }
            catch (Exception ex) { Log.Write(T("log.actionError", "GPU", ex.Message)); }
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

                // a game started through GameSwitcher keeps its session while it is still loading
                if (active != null && running.Any(g => g.Key == active.Key)) launchWait = DateTime.MinValue;
                bool waitingForLaunch = active != null && paused == null && DateTime.Now < launchWait;

                if (running.Count > 0 && active == null) StartSession(running[0]);
                else if (running.Count > 0 || waitingForLaunch) idleSeconds = 0;
                else if (active != null)
                {
                    idleSeconds++;
                    // manual mode and pause end at once; a game gets a grace period for launcher restarts
                    if (active.Key == "manual" || paused != null || idleSeconds >= IdleSecondsBeforeRevert) StopSession();
                }
                UpdateCouch(paused != null);
                UpdateWinKey();
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


        #region Windows key

        WinKeyBlocker winKey;
        HashSet<int> gamePids = new HashSet<int>();

        // Blocks the Windows key while the active game (not the manual game mode) is in the foreground
        void UpdateWinKey()
        {
            bool wanted = active != null && active != ManualGame && config.ActionsFor(active.Key).On("WinKey");
            if (wanted)
            {
                var names = new HashSet<string>(active.Exes.Select(e => Path.GetFileNameWithoutExtension(e).ToLowerInvariant()));
                var pids = new HashSet<int>();
                foreach (Process p in Process.GetProcesses())
                {
                    if (names.Contains(p.ProcessName.ToLowerInvariant())) pids.Add(p.Id);
                    p.Dispose();
                }
                gamePids = pids;
                if (winKey == null)
                {
                    try { winKey = new WinKeyBlocker(pid => gamePids.Contains(pid)); Log.Write(T("log.winKeyBlocked")); }
                    catch (Exception ex) { Log.Write(T("log.actionError", T("act.winKey"), ex.Message)); }
                }
            }
            else if (winKey != null)
            {
                winKey.Dispose();
                winKey = null;
                gamePids = new HashSet<int>();
            }
        }

        #endregion
        #region Batteries and couch mode

        // Devices are polled on a background thread: HID requests can take a moment
        readonly ManualResetEvent stopPolling = new ManualResetEvent(false);
        Thread poller;
        volatile List<Battery> batteries = new List<Battery>();
        volatile HashSet<string> controllers;   // connected controllers (null: not polled)
        readonly HashSet<string> batteryWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CouchState couch;
        readonly CouchTrigger couchTrigger = new CouchTrigger();
        DateTime controllerLost = DateTime.MaxValue;
        bool couchWanted;
        const int CouchGraceSeconds = 15;

        void StartDevicePolling()
        {
            poller = new Thread(() =>
            {
                DateTime lastBattery = DateTime.MinValue;
                bool batteryShown = false;
                while (!stopPolling.WaitOne(3000))
                {
                    try
                    {
                        Config c = config;
                        controllers = c.Couch.Enabled ? Batteries.ConnectedControllers() : null;
                        if (c.Battery && (DateTime.UtcNow - lastBattery).TotalSeconds >= 60)
                        {
                            lastBattery = DateTime.UtcNow;
                            var list = Batteries.Read();
                            batteries = list;
                            batteryShown = true;
                            Json.WriteFile(Paths.Data("battery.json"), new Dictionary<string, object> { ["Time"] = DateTime.Now.ToString("o"), ["Devices"] = list });
                            invoker.BeginInvoke((Action)CheckBatteries);
                        }
                        else if (!c.Battery && batteryShown)
                        {
                            batteryShown = false;
                            lastBattery = DateTime.MinValue;
                            batteries = new List<Battery>();
                            try { File.Delete(Paths.Data("battery.json")); } catch { }
                        }
                    }
                    catch (Exception ex) { Log.Write(T("log.error", ex.Message)); }
                }
            }) { IsBackground = true, Name = "Device polling" };
            poller.Start();
        }

        List<Battery> LowBatteries() =>
            batteries.Where(b => !b.Charging && b.Estimate() >= 0 && b.Estimate() <= config.BatteryThreshold).ToList();

        string LowBatteryText(string key)
        {
            var low = LowBatteries();
            return config.Battery && low.Count > 0 ? T(key, string.Join(", ", low.Select(b => b.Text()))) : null;
        }

        // Warns once when a battery drops to the threshold; again only after it was charged
        void CheckBatteries()
        {
            if (!config.Battery || !config.BatteryWarn) return;
            var low = LowBatteries();
            foreach (var b in low.Where(b => !batteryWarned.Contains(b.Name)))
            {
                batteryWarned.Add(b.Name);
                tray.ShowBalloonTip(8000, "GameSwitcher", T("notify.batteryLow", b.Text()), ToolTipIcon.Warning);
                Log.Write(T("notify.batteryLow", b.Text()));
            }
            foreach (var b in batteries.Where(b => b.Charging || b.Estimate() > config.BatteryThreshold + 5)) batteryWarned.Remove(b.Name);
        }

        // A couch mode left active by a crash or restart: revert it unless a controller is still connected
        void RecoverCouchState()
        {
            CouchState pending = Couch.LoadPending();
            if (pending == null) return;
            if (config.Couch.Enabled && Batteries.ConnectedControllers().Count > 0)
            {
                couch = pending;
                couchWanted = true;
                couchTrigger.Reset(ignoreCurrent: false);   // the controllers in use keep couch mode on
            }
            else Couch.End(config.Couch, pending);
        }

        void UpdateCouch(bool paused)
        {
            if (!config.Couch.Enabled)
            {
                if (couch != null) { Couch.End(config.Couch, couch); couch = null; }
                couchTrigger.Reset();
                couchWanted = false;
                return;
            }
            var connected = controllers;
            if (connected == null) return;
            bool wanted = couchTrigger.Update(connected);
            if (wanted)
            {
                controllerLost = DateTime.MaxValue;
                if (!couchWanted && couch == null && !paused)
                {
                    couch = Couch.Start(config.Couch);
                    if (config.Notifications) Notify(T("notify.couchOn"));
                }
            }
            else if (couch != null)
            {
                if (controllerLost == DateTime.MaxValue) controllerLost = DateTime.Now;
                else if ((DateTime.Now - controllerLost).TotalSeconds >= CouchGraceSeconds)
                {
                    Couch.End(config.Couch, couch);
                    couch = null;
                    controllerLost = DateTime.MaxValue;
                    if (config.Notifications) Notify(T("notify.couchOff"));
                }
            }
            couchWanted = wanted;
        }

        // Battery levels and recently played games in the tray menu, rebuilt each time it opens
        void UpdateMenuExtras()
        {
            foreach (var item in batteryItems) { menu.Items.Remove(item); item.Dispose(); }
            batteryItems.Clear();
            if (config.Battery && batteries.Count > 0)
            {
                int at = menu.Items.IndexOf(miStatus) + 1;
                foreach (var b in batteries.OrderBy(b => b.Name))
                {
                    var item = new ToolStripMenuItem(T("battery.label") + " " + b.Text()) { Enabled = false };
                    menu.Items.Insert(at++, item);
                    batteryItems.Add(item);
                }
            }

            miRecent.Text = T("tray.recent");
            miRecent.Visible = config.RecentInTray;
            if (!config.RecentInTray) return;
            foreach (ToolStripItem old in miRecent.DropDownItems.Cast<ToolStripItem>().ToList()) old.Dispose();
            miRecent.DropDownItems.Clear();
            var playtime = Json.ReadFile(Paths.Data("playtime.json")) ?? new Dictionary<string, object>();
            var recent = playtime.Where(kv => kv.Value is Dictionary<string, object>)
                .Select(kv => (Key: kv.Key, Data: (Dictionary<string, object>)kv.Value))
                .Select(g => (g.Key, Name: g.Data.Str("Name"), Last: DateTime.TryParse(g.Data.Str("LastPlayed"), null, DateTimeStyles.RoundtripKind, out DateTime d) ? d : DateTime.MinValue))
                .Where(g => g.Key != "manual" && g.Name.Length > 0)
                .OrderByDescending(g => g.Last).Take(5).ToList();
            foreach (var g in recent)
            {
                string key = g.Key;
                var item = new ToolStripMenuItem(g.Name) { Enabled = Launcher.TargetFor(config, key) != null };
                item.Click += (s, e) => LaunchWithProfile(key);
                miRecent.DropDownItems.Add(item);
            }
            if (recent.Count == 0) miRecent.DropDownItems.Add(new ToolStripMenuItem(T("tray.recentNone")) { Enabled = false });
        }

        // Applies the game's profile first, then starts the game; the session waits up to two minutes
        // for the game to appear (launchers, updates). A paused service or a running game just starts it.
        DateTime launchWait = DateTime.MinValue;
        const int LaunchWaitSeconds = 120;

        void LaunchWithProfile(string key)
        {
            string target = Launcher.TargetFor(config, key);
            if (target == null) return;
            GameRef game = config.Selected.FirstOrDefault(g => g.Key == key)
                ?? autoMap.Values.FirstOrDefault(g => g.Key == key);   // only games the service also detects while running
            try
            {
                if (game != null && active == null && Pause.Get() == null)
                {
                    Log.Write(T("log.launch", game.Name));
                    StartSession(game);
                    launchWait = DateTime.Now.AddSeconds(LaunchWaitSeconds);
                }
                Launcher.Start(target);
            }
            catch (Exception ex) { Log.Write(T("log.error", ex.Message)); }
            UpdateTrayTexts(Pause.Get());
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
        // The window, the tray menu and desktop shortcuts signal this event to start a game through the service
        readonly EventWaitHandle launchRequest = new EventWaitHandle(false, EventResetMode.AutoReset, Launcher.RequestEvent);
        readonly Control invoker = new Control();

        void ListenForExitRequest()
        {
            invoker.CreateControl();
            ThreadPool.RegisterWaitForSingleObject(exitRequest, (s, timedOut) => invoker.BeginInvoke((Action)ExitService), null, -1, true);
            ThreadPool.RegisterWaitForSingleObject(launchRequest, (s, timedOut) => invoker.BeginInvoke((Action)(() => { string key = Launcher.TakeRequest(); if (key != null) LaunchWithProfile(key); })), null, -1, false);
        }

        void ExitService()
        {
            timer.Stop();
            if (active != null) StopSession();
            stopPolling.Set();
            if (couch != null) { Couch.End(config.Couch, couch); couch = null; }
            tray.Visible = false;
            hotkey.Unregister();
            winKey?.Dispose(); winKey = null;
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
