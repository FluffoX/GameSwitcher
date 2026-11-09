using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using GameSwitcher.Native;
using static GameSwitcher.Strings;

namespace GameSwitcher.Gui
{
    // Simple item for combo and list boxes: shows Name, carries Code
    class Item
    {
        public string Code, Name;
        public bool Disconnected;
        public override string ToString() => Name;
    }

    // Sorts by the SubItem's Tag when set (numbers, dates), otherwise by text
    class ListViewSorter : IComparer
    {
        public int Column;
        public bool Descending;
        public int Compare(object a, object b)
        {
            var x = ((ListViewItem)a).SubItems[Column];
            var y = ((ListViewItem)b).SubItems[Column];
            int r = x.Tag is IComparable cx && y.Tag != null && x.Tag.GetType() == y.Tag.GetType()
                ? cx.CompareTo(y.Tag)
                : string.Compare(x.Text, y.Text, StringComparison.CurrentCultureIgnoreCase);
            return Descending ? -r : r;
        }
    }

    class MainForm : Form
    {
        static readonly Color ColorOn = Color.FromArgb(0, 150, 70), ColorOff = Color.Gray;
        static readonly string[] RemovableSources = { "Custom", "Folder", "Missing" };

        readonly GuiSettings settings;
        readonly bool demo;
        List<Game> games = new List<Game>();
        readonly float scale;
        readonly int innerWidth;
        bool loading, serviceRunning, nightLightSupported;
        string editKey = "";
        int tick;
        ListBox addTarget;
        List<HdrDisplay> hdrDisplays = new List<HdrDisplay>();
        string updateTag;
        Dictionary<string, Dictionary<string, object>> demoPlaytime;

        // Controls
        readonly ListView list = new ListView();
        readonly ImageList images = new ImageList();
        readonly ListViewSorter sorter = new ListViewSorter();
        readonly Label lblHdr, lblRunning, lblAudio, lblPause, lblSearch, lblActions, lblProfile, lblScriptStart, lblScriptEnd;
        readonly Label secDisplay, secAudio, secSystem, secPrograms;
        readonly Button btnToggleHdr, btnPause, btnRescan, btnAddExe, btnAddFolder, btnRemove, btnLog, btnSettings;
        readonly Button btnCloseAdd, btnCloseRemove, btnStartAdd, btnStartRemove, btnScriptStart, btnScriptEnd;
        readonly LinkLabel lnkUpdate = new LinkLabel();
        readonly TextBox txtSearch = new TextBox(), txtScriptStart = new TextBox(), txtScriptEnd = new TextBox();
        readonly ComboBox cmbSource, cmbProfile, cmbDisplay, cmbAudio, cmbMic, cmbPower;
        readonly CheckBox chkOnlySelected, chkOwnProfile;
        readonly CheckBox chkHdr, chkHdrRevert, chkSdr, chkSdrRevert, chkDisplay, chkDisplayRevert, chkMonitors, chkMonitorsRevert,
                          chkNight, chkNightRevert, chkAudio, chkAudioRevert, chkMic, chkMicRevert, chkVolume, chkVolumeRevert,
                          chkPower, chkPowerRevert, chkKeepAwake, chkClose, chkCloseRevert, chkStart, chkStartRevert, chkScript;
        readonly NumericUpDown numSdr, numVolume;
        readonly ListBox lstClose, lstStart;
        readonly FlowLayoutPanel statusPanel, searchPanel, bottom, actionsPanel;
        readonly StatusStrip statusBar = new StatusStrip();
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel();
        readonly ContextMenuStrip menu = new ContextMenuStrip(), addMenu = new ContextMenuStrip(), pauseMenu = new ContextMenuStrip();
        readonly ToolStripMenuItem miLaunch, miProfile, miOpen, miRemove, miAddRunning, miAddFile, miPause1, miPause2, miPauseReboot;
        readonly Timer timer = new Timer { Interval = 2000 };
        readonly List<(Control Control, string Action, string Field)> bindings;
        readonly List<(CheckBox Master, Control[] Dependents)> dependents;

        public MainForm(GuiSettings settings, bool demo)
        {
            this.settings = settings;
            this.demo = demo;
            Text = "GameSwitcher";
            Font = new Font("Segoe UI", 9);
            string iconPath = Path.Combine(Paths.AppDir, "icon.ico");
            if (File.Exists(iconPath)) Icon = new Icon(iconPath);
            scale = DeviceDpi / 96f;
            ClientSize = new Size(Px(1320), Px(760));
            StartPosition = FormStartPosition.CenterScreen;
            innerWidth = Px(375);
            nightLightSupported = demo || NightLight.Get() != null;

            // Game list
            list.Dock = DockStyle.Fill; list.View = View.Details; list.CheckBoxes = true; list.FullRowSelect = true;
            list.HideSelection = false; list.MultiSelect = false; list.ShowItemToolTips = true;
            foreach (int w in new[] { 290, 90, 80, 85, 105, 300 }) list.Columns.Add("", Px(w));
            images.ColorDepth = ColorDepth.Depth32Bit; images.ImageSize = new Size(Px(16), Px(16));
            list.SmallImageList = images;
            list.ListViewItemSorter = sorter;

            // Top: HDR state, running game, audio, pause, update notice
            statusPanel = NewFlow(DockStyle.Top, new Padding(6, 8, 6, 2));
            lblHdr = NewLabel(new Padding(3, 4, 12, 3));
            btnToggleHdr = NewButton();
            lblRunning = NewLabel(new Padding(18, 8, 3, 3));
            lblAudio = NewLabel(new Padding(18, 8, 3, 3));
            btnPause = NewButton(); btnPause.Margin = new Padding(24, 3, 3, 3);
            lblPause = NewLabel(new Padding(6, 8, 3, 3));
            lnkUpdate.AutoSize = true; lnkUpdate.Margin = new Padding(18, 8, 3, 3); lnkUpdate.Visible = false;
            statusPanel.Controls.AddRange(new Control[] { lblHdr, btnToggleHdr, lblRunning, lblAudio, btnPause, lblPause, lnkUpdate });

            // Search and source filter
            searchPanel = NewFlow(DockStyle.Top, new Padding(6, 2, 6, 2));
            lblSearch = NewLabel(new Padding(3, 7, 3, 3));
            txtSearch.Width = Px(240);
            cmbSource = NewCombo(Px(140)); cmbSource.Margin = new Padding(12, 3, 3, 3);
            chkOnlySelected = new CheckBox { AutoSize = true, Margin = new Padding(12, 5, 3, 3) };
            btnRescan = NewButton();
            searchPanel.Controls.AddRange(new Control[] { lblSearch, txtSearch, cmbSource, chkOnlySelected, btnRescan });

            // Bottom buttons
            bottom = NewFlow(DockStyle.Bottom, new Padding(6, 4, 6, 6));
            btnAddExe = NewButton(); btnAddFolder = NewButton();
            btnRemove = NewButton(); btnRemove.Enabled = false;
            btnLog = NewButton(); btnLog.Margin = new Padding(24, 3, 3, 3);
            btnSettings = NewButton();
            bottom.Controls.AddRange(new Control[] { btnAddExe, btnAddFolder, btnRemove, btnLog, btnSettings });

            statusBar.Items.Add(status);

            // Right: actions on game start
            actionsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Right, Width = Px(430), FlowDirection = FlowDirection.TopDown,
                WrapContents = false, AutoScroll = true, Padding = new Padding(10, 4, 10, 4)
            };
            lblActions = NewLabel(new Padding(3, 6, 3, 4));
            var profileRow = NewFlow();
            lblProfile = NewLabel(new Padding(3, 7, 3, 3));
            cmbProfile = NewCombo(innerWidth - Px(80));
            profileRow.Controls.AddRange(new Control[] { lblProfile, cmbProfile });
            chkOwnProfile = NewCheck(0, 4);

            secDisplay = NewSection();
            chkHdr = NewCheck(0, 2); chkHdrRevert = NewCheck(20);
            var sdrRow = NewCheckNumber(8, out chkSdr, out numSdr); chkSdrRevert = NewCheck(20);
            chkDisplay = NewCheck(0, 10); cmbDisplay = NewActionCombo(); chkDisplayRevert = NewCheck(20);
            chkMonitors = NewCheck(0, 10); chkMonitorsRevert = NewCheck(20);
            chkNight = NewCheck(0, 10); chkNightRevert = NewCheck(20);

            secAudio = NewSection();
            chkAudio = NewCheck(0, 2); cmbAudio = NewActionCombo(); chkAudioRevert = NewCheck(20);
            chkMic = NewCheck(0, 10); cmbMic = NewActionCombo(); chkMicRevert = NewCheck(20);
            var volRow = NewCheckNumber(8, out chkVolume, out numVolume); chkVolumeRevert = NewCheck(20);

            secSystem = NewSection();
            chkPower = NewCheck(0, 2); cmbPower = NewActionCombo(); chkPowerRevert = NewCheck(20);
            chkKeepAwake = NewCheck(0, 10);

            secPrograms = NewSection();
            chkClose = NewCheck(0, 2); lstClose = NewAppList();
            var closeRow = NewButtonRow(out btnCloseAdd, out btnCloseRemove); chkCloseRevert = NewCheck(20);
            chkStart = NewCheck(0, 10); lstStart = NewAppList();
            var startRow = NewButtonRow(out btnStartAdd, out btnStartRemove); chkStartRevert = NewCheck(20);
            chkScript = NewCheck(0, 10);
            lblScriptStart = NewLabel(new Padding(23, 4, 3, 0)); var scriptStartRow = NewScriptRow(txtScriptStart, out btnScriptStart);
            lblScriptEnd = NewLabel(new Padding(23, 4, 3, 0)); var scriptEndRow = NewScriptRow(txtScriptEnd, out btnScriptEnd);

            actionsPanel.Controls.AddRange(new Control[] {
                lblActions, profileRow, chkOwnProfile,
                secDisplay, chkHdr, chkHdrRevert, sdrRow, chkSdrRevert, chkDisplay, cmbDisplay, chkDisplayRevert,
                chkMonitors, chkMonitorsRevert, chkNight, chkNightRevert,
                secAudio, chkAudio, cmbAudio, chkAudioRevert, chkMic, cmbMic, chkMicRevert, volRow, chkVolumeRevert,
                secSystem, chkPower, cmbPower, chkPowerRevert, chkKeepAwake,
                secPrograms, chkClose, lstClose, closeRow, chkCloseRevert, chkStart, lstStart, startRow, chkStartRevert,
                chkScript, lblScriptStart, scriptStartRow, lblScriptEnd, scriptEndRow });

            // Simple fields: control -> action/field, and which controls depend on which checkbox
            bindings = new List<(Control, string, string)>
            {
                (chkHdr, "Hdr", "Enabled"), (chkHdrRevert, "Hdr", "Revert"),
                (chkSdr, "Sdr", "Enabled"), (numSdr, "Sdr", "Level"), (chkSdrRevert, "Sdr", "Revert"),
                (chkDisplay, "Display", "Enabled"), (chkDisplayRevert, "Display", "Revert"),
                (chkMonitors, "Monitors", "Enabled"), (chkMonitorsRevert, "Monitors", "Revert"),
                (chkNight, "NightLight", "Enabled"), (chkNightRevert, "NightLight", "Revert"),
                (chkAudio, "Audio", "Enabled"), (chkAudioRevert, "Audio", "Revert"),
                (chkMic, "Mic", "Enabled"), (chkMicRevert, "Mic", "Revert"),
                (chkVolume, "Volume", "Enabled"), (numVolume, "Volume", "Level"), (chkVolumeRevert, "Volume", "Revert"),
                (chkPower, "Power", "Enabled"), (chkPowerRevert, "Power", "Revert"),
                (chkKeepAwake, "KeepAwake", "Enabled"),
                (chkClose, "CloseApps", "Enabled"), (chkCloseRevert, "CloseApps", "Revert"),
                (chkStart, "StartApps", "Enabled"), (chkStartRevert, "StartApps", "Revert"),
                (chkScript, "Script", "Enabled"), (txtScriptStart, "Script", "Start"), (txtScriptEnd, "Script", "End"),
            };
            dependents = new List<(CheckBox, Control[])>
            {
                (chkHdr, new Control[] { chkHdrRevert }), (chkSdr, new Control[] { numSdr, chkSdrRevert }),
                (chkDisplay, new Control[] { cmbDisplay, chkDisplayRevert }), (chkMonitors, new Control[] { chkMonitorsRevert }),
                (chkNight, new Control[] { chkNightRevert }), (chkAudio, new Control[] { cmbAudio, chkAudioRevert }),
                (chkMic, new Control[] { cmbMic, chkMicRevert }), (chkVolume, new Control[] { numVolume, chkVolumeRevert }),
                (chkPower, new Control[] { cmbPower, chkPowerRevert }),
                (chkClose, new Control[] { lstClose, btnCloseAdd, btnCloseRemove, chkCloseRevert }),
                (chkStart, new Control[] { lstStart, btnStartAdd, btnStartRemove, chkStartRevert }),
                (chkScript, new Control[] { lblScriptStart, txtScriptStart, btnScriptStart, lblScriptEnd, txtScriptEnd, btnScriptEnd }),
            };

            // Menus
            miLaunch = AddMenuItem(menu, () => StartGame(SelectedGame)); miLaunch.Font = new Font(miLaunch.Font, FontStyle.Bold);
            miProfile = AddMenuItem(menu, EditSelectedProfile);
            miOpen = AddMenuItem(menu, OpenSelectedLocation);
            miRemove = AddMenuItem(menu, RemoveSelectedGame);
            list.ContextMenuStrip = menu;
            miAddRunning = AddMenuItem(addMenu, () => AddAppToList(addTarget, ProcessPicker.Pick(this)));
            miAddFile = AddMenuItem(addMenu, () =>
            {
                using (var dlg = new OpenFileDialog { Title = T("dlg.chooseProgram"), Filter = T("dlg.exeFilter") })
                    if (dlg.ShowDialog(this) == DialogResult.OK) AddAppToList(addTarget, dlg.FileName);
            });
            miPause1 = AddMenuItem(pauseMenu, () => SetPause(1));
            miPause2 = AddMenuItem(pauseMenu, () => SetPause(2));
            miPauseReboot = AddMenuItem(pauseMenu, () => SetPause(0, true));

            // Order matters: add the Fill control first and the outermost bars last
            Controls.AddRange(new Control[] { list, actionsPanel, searchPanel, statusPanel, bottom, statusBar });

            WireEvents();
            SetUiTexts();
        }

        #region Control factories

        int Px(double v) => (int)(v * scale);

        static ToolStripMenuItem AddMenuItem(ContextMenuStrip m, Action onClick)
        {
            var item = new ToolStripMenuItem();
            item.Click += (s, e) => onClick();
            m.Items.Add(item);
            return item;
        }

        Button NewButton() => new Button
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 0, 6, 0),
            MinimumSize = new Size(Px(80), Px(26))
        };

        static Label NewLabel(Padding margin) => new Label { AutoSize = true, Margin = margin };

        static FlowLayoutPanel NewFlow(DockStyle dock = DockStyle.None, Padding padding = default) => new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = dock != DockStyle.None, Margin = Padding.Empty, Padding = padding, Dock = dock
        };

        static ComboBox NewCombo(int width) => new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };

        ComboBox NewActionCombo()
        {
            var c = NewCombo(innerWidth - Px(24));
            c.Margin = new Padding(23, 3, 3, 2);
            return c;
        }

        CheckBox NewCheck(int indent = 0, int top = 2) =>
            new CheckBox { AutoSize = true, Margin = new Padding(3 + indent, top, 3, 1), Tag = innerWidth - indent };

        // A checkbox followed by a number field and a unit, e.g. "Set volume to [50] %"
        FlowLayoutPanel NewCheckNumber(int top, out CheckBox check, out NumericUpDown number)
        {
            var row = NewFlow();
            check = NewCheck(0, top); check.Tag = innerWidth - Px(100);
            number = new NumericUpDown { Minimum = 0, Maximum = 100, Width = Px(60), Margin = new Padding(3, top - 2, 3, 1) };
            var unit = NewLabel(new Padding(0, top + 2, 3, 1)); unit.Text = "%";
            row.Controls.AddRange(new Control[] { check, number, unit });
            return row;
        }

        Label NewSection() => NewLabel(new Padding(3, 16, 3, 4));

        ListBox NewAppList() => new ListBox
        {
            Width = innerWidth - Px(24), Height = Px(58), Margin = new Padding(23, 3, 3, 2), HorizontalScrollbar = true
        };

        FlowLayoutPanel NewButtonRow(out Button add, out Button remove)
        {
            var row = NewFlow(); row.Margin = new Padding(20, 0, 0, 0);
            add = NewButton(); remove = NewButton();
            row.Controls.AddRange(new Control[] { add, remove });
            return row;
        }

        FlowLayoutPanel NewScriptRow(TextBox text, out Button browse)
        {
            var row = NewFlow(); row.Margin = new Padding(20, 0, 0, 0);
            text.Width = innerWidth - Px(70); text.Margin = new Padding(3);
            browse = NewButton(); browse.Text = "…"; browse.MinimumSize = new Size(Px(30), Px(24));
            row.Controls.AddRange(new Control[] { text, browse });
            return row;
        }

        #endregion

        #region Texts, theme, language

        void SetUiTexts()
        {
            string font = FontName;
            Font = new Font(font, 9);
            lblHdr.Font = new Font(font, 11, FontStyle.Bold);
            lblActions.Font = new Font(font, 10, FontStyle.Bold);
            foreach (var sec in new[] { secDisplay, secAudio, secSystem, secPrograms }) sec.Font = new Font(font, 9.5f, FontStyle.Bold);

            string[] columns = { "col.game", "col.source", "col.profile", "col.playtime", "col.lastPlayed", "col.processes" };
            for (int i = 0; i < columns.Length; i++) list.Columns[i].Text = T(columns[i]);
            lblSearch.Text = T("search.label");
            chkOnlySelected.Text = T("search.onlySelected");
            btnRescan.Text = T("btn.rescan");
            btnAddExe.Text = T("btn.addExe");
            btnAddFolder.Text = T("btn.addFolder");
            btnRemove.Text = T("btn.remove");
            btnLog.Text = T("btn.log");
            btnSettings.Text = T("btn.settings");
            lblActions.Text = T("actions.title");
            lblProfile.Text = T("profile.label");
            chkOwnProfile.Text = T("profile.own");
            secDisplay.Text = T("sec.display"); secAudio.Text = T("sec.audio"); secSystem.Text = T("sec.system"); secPrograms.Text = T("sec.programs");
            chkHdr.Text = T("act.hdr"); chkHdrRevert.Text = T("act.hdrRevert");
            chkSdr.Text = T("act.sdr"); chkSdrRevert.Text = T("act.sdrRevert");
            chkDisplay.Text = T("act.display"); chkDisplayRevert.Text = T("act.displayRevert");
            chkMonitors.Text = T("act.monitors"); chkMonitorsRevert.Text = T("act.monitorsRevert");
            chkNight.Text = T("act.nightLight") + (nightLightSupported ? "" : " – " + T("label.unsupported"));
            chkNightRevert.Text = T("act.nightLightRevert");
            chkAudio.Text = T("act.audio"); chkAudioRevert.Text = T("act.audioRevert");
            chkMic.Text = T("act.mic"); chkMicRevert.Text = T("act.micRevert");
            chkVolume.Text = T("act.volume"); chkVolumeRevert.Text = T("act.volumeRevert");
            chkPower.Text = T("act.power"); chkPowerRevert.Text = T("act.powerRevert");
            chkKeepAwake.Text = T("act.keepAwake");
            chkClose.Text = T("act.close"); chkCloseRevert.Text = T("act.closeRevert");
            chkStart.Text = T("act.start"); chkStartRevert.Text = T("act.startRevert");
            chkScript.Text = T("act.script");
            lblScriptStart.Text = T("act.scriptStart"); lblScriptEnd.Text = T("act.scriptEnd");
            btnCloseAdd.Text = btnStartAdd.Text = T("btn.add");
            btnCloseRemove.Text = btnStartRemove.Text = T("btn.remove");
            miLaunch.Text = T("menu.launch"); miProfile.Text = T("menu.editProfile");
            miOpen.Text = T("menu.openLocation"); miRemove.Text = T("menu.removeFromList");
            miAddRunning.Text = T("menu.addRunning"); miAddFile.Text = T("menu.addFile");
            miPause1.Text = T("pause.1h"); miPause2.Text = T("pause.2h"); miPauseReboot.Text = T("pause.reboot");
            if (updateTag != null) lnkUpdate.Text = T("update.available", updateTag);
        }

        public string ThemeSetting => settings.Theme;

        public void ApplyTheme() => Theme.Apply(this, Theme.IsDark(settings.Theme), new[] { menu, addMenu, pauseMenu }, lblHdr);

        public void ChangeLanguage(string code)
        {
            LoadCode(code);
            settings.Language = code;
            Save();
            SetUiTexts();
            UpdateHdr(); UpdateRunning(); UpdateAudioLabel(); UpdatePauseLabel();
            UpdateSourceFilter(); UpdateProfileList(); ShowGames();
            ApplyTheme();
        }

        public void ChangeTheme(string theme)
        {
            settings.Theme = theme;
            Save();
            ApplyTheme();
        }

        #endregion

        #region Status displays

        public void Save() => settings.Save(games);

        public void UpdateServiceState() => serviceRunning = !demo && Autostart.ServiceRunning();
        public bool ServiceIsRunning => serviceRunning;

        void UpdateStatus() =>
            status.Text = T("status.summary", settings.Selected.Count, games.Count, serviceRunning ? T("service.running") : T("service.stopped"));

        void UpdateHdr()
        {
            if (demo)
            {
                hdrDisplays = new List<HdrDisplay>();
                lblHdr.Text = "● " + T("hdr.off") + "  –  Monitor"; lblHdr.ForeColor = ColorOff; btnToggleHdr.Text = T("btn.hdrOn");
                return;
            }
            try { hdrDisplays = DisplayConfig.GetDisplays().Where(d => d.HdrSupported).ToList(); } catch { hdrDisplays = new List<HdrDisplay>(); }
            if (hdrDisplays.Count == 0)
            {
                lblHdr.Text = "● " + T("hdr.none"); lblHdr.ForeColor = ColorOff;
                btnToggleHdr.Text = T("btn.hdrOn"); btnToggleHdr.Enabled = false;
                return;
            }
            int on = hdrDisplays.Count(d => d.HdrEnabled);
            string state = on == hdrDisplays.Count ? T("hdr.on") : on > 0 ? T("hdr.partial") : T("hdr.off");
            string names = hdrDisplays.Count == 1 ? hdrDisplays[0].Name
                : string.Join(", ", hdrDisplays.Select(d => d.Name + ": " + (d.HdrEnabled ? T("hdr.displayOn") : T("hdr.displayOff"))));
            lblHdr.Text = $"● {state}  –  {names}";
            lblHdr.ForeColor = on > 0 ? ColorOn : ColorOff;
            btnToggleHdr.Enabled = true;
            btnToggleHdr.Text = on > 0 ? T("btn.hdrOff") : T("btn.hdrOn");
        }

        void UpdateRunning()
        {
            var map = new Dictionary<string, string>();
            foreach (var s in settings.Selected.Values)
                foreach (string x in s.Exes) map[Path.GetFileNameWithoutExtension(x).ToLowerInvariant()] = s.Name;
            var names = new SortedSet<string>();
            if (map.Count > 0)
                foreach (Process p in Process.GetProcesses())
                {
                    if (map.TryGetValue(p.ProcessName.ToLowerInvariant(), out string n)) names.Add(n);
                    p.Dispose();
                }
            lblRunning.Text = names.Count > 0 ? T("running.games", string.Join(", ", names)) : T("running.none");
        }

        void UpdateAudioLabel()
        {
            string name = demo ? "Headset (USB Audio)" : SafeGet(() => Audio.GetDefaultName(Audio.Output));
            lblAudio.Text = T("audio.current", string.IsNullOrEmpty(name) ? "–" : name);
        }

        static T2 SafeGet<T2>(Func<T2> f) { try { return f(); } catch { return default(T2); } }

        void UpdatePauseLabel()
        {
            var p = demo ? null : Pause.Get();
            lblPause.Text = Pause.Describe(p);
            btnPause.Text = p != null ? T("btn.resume") : T("btn.pause");
        }

        void SetPause(int hours, bool untilReboot = false)
        {
            if (demo) return;
            Pause.Set(hours, untilReboot);
            UpdatePauseLabel();
        }

        #endregion

        #region Game list

        static string SourceText(string source) =>
            source == "Custom" ? T("source.custom") : source == "Folder" ? T("source.folder") : source == "Missing" ? T("source.missing") : source;

        static string FormatDuration(long seconds)
        {
            if (seconds < 60) return "";
            long h = seconds / 3600, m = seconds % 3600 / 60;
            return h > 0 ? T("time.hm", h, m) : T("time.m", m);
        }

        Dictionary<string, Dictionary<string, object>> ReadPlaytime()
        {
            if (demo) return demoPlaytime;
            var raw = Json.ReadFile(Paths.Data("playtime.json"));
            return raw == null ? new Dictionary<string, Dictionary<string, object>>()
                : raw.Where(kv => kv.Value is Dictionary<string, object>).ToDictionary(kv => kv.Key, kv => (Dictionary<string, object>)kv.Value);
        }

        int IconIndex(Game game)
        {
            if (string.IsNullOrEmpty(game.MainExe) || !File.Exists(game.MainExe)) return -1;
            if (!images.Images.ContainsKey(game.Key))
            {
                try { images.Images.Add(game.Key, Icon.ExtractAssociatedIcon(game.MainExe)); } catch { return -1; }
            }
            return images.Images.IndexOfKey(game.Key);
        }

        Game SelectedGame => list.SelectedItems.Count > 0 ? (Game)list.SelectedItems[0].Tag : null;

        void UpdateRemoveButton() => btnRemove.Enabled = SelectedGame != null && RemovableSources.Contains(SelectedGame.Source);

        string ProfileText(Game g) =>
            !settings.Selected.ContainsKey(g.Key) ? "" : settings.Profiles.ContainsKey(g.Key) ? T("profile.ownShort") : T("profile.defaultShort");

        void SetPlaytimeCells(ListViewItem item, Dictionary<string, Dictionary<string, object>> playtime)
        {
            playtime.TryGetValue(((Game)item.Tag).Key, out var p);
            long secs = (long)p.Double("Seconds");
            item.SubItems[3].Text = FormatDuration(secs);
            item.SubItems[3].Tag = secs;
            DateTime? last = DateTime.TryParse(p.Str("LastPlayed"), null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime d) ? d.ToLocalTime() : (DateTime?)null;
            item.SubItems[4].Text = last?.ToString("d") ?? "";
            item.SubItems[4].Tag = last?.Ticks ?? 0L;
        }

        void UpdateSourceFilter()
        {
            string current = (cmbSource.SelectedItem as Item)?.Code ?? "";
            loading = true;
            cmbSource.Items.Clear();
            var all = new Item { Code = "", Name = T("filter.allSources") };
            cmbSource.Items.Add(all);
            cmbSource.SelectedItem = all;
            foreach (string src in games.Select(g => g.Source).Distinct().OrderBy(s => s))
            {
                var i = new Item { Code = src, Name = SourceText(src) };
                cmbSource.Items.Add(i);
                if (src == current) cmbSource.SelectedItem = i;
            }
            loading = false;
        }

        void ShowGames()
        {
            loading = true;
            var playtime = ReadPlaytime();
            string keep = SelectedGame?.Key;
            string filter = txtSearch.Text.Trim();
            string source = (cmbSource.SelectedItem as Item)?.Code ?? "";
            list.BeginUpdate();
            list.Items.Clear();
            foreach (Game game in games)
            {
                bool isSelected = settings.Selected.ContainsKey(game.Key);
                if (chkOnlySelected.Checked && !isSelected) continue;
                if (source.Length > 0 && game.Source != source) continue;
                if (filter.Length > 0 && game.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) < 0 &&
                    string.Join(" ", game.Exes).IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                var item = new ListViewItem(game.Name) { ImageIndex = IconIndex(game), Checked = isSelected, Tag = game, ToolTipText = game.Path };
                item.SubItems.Add(SourceText(game.Source));
                item.SubItems.Add(ProfileText(game));
                item.SubItems.Add(""); item.SubItems.Add("");
                item.SubItems.Add(string.Join(", ", game.Exes));
                if (game.Source == "Missing") item.ForeColor = ColorOff;
                SetPlaytimeCells(item, playtime);
                list.Items.Add(item);
                if (game.Key == keep) item.Selected = true;
            }
            list.Sort();
            list.EndUpdate();
            loading = false;
            UpdateRemoveButton();
            UpdateStatus();
        }

        void UpdatePlaytimeColumns()
        {
            var playtime = ReadPlaytime();
            foreach (ListViewItem item in list.Items) SetPlaytimeCells(item, playtime);
        }

        void Scan()
        {
            Cursor = Cursors.WaitCursor;
            status.Text = T("status.scanning");
            Application.DoEvents();
            games = demo ? DemoGames() : GameLibrary.FindAll(settings.CustomFolders, settings.CustomExes, settings.Selected.Values);
            Save();
            Cursor = Cursors.Default;
            UpdateSourceFilter();
            UpdateProfileList();
            ShowGames();
        }

        void RemoveSelectedGame()
        {
            Game game = SelectedGame;
            if (game == null) return;
            switch (game.Source)
            {
                case "Custom":
                    settings.CustomExes.RemoveAll(e => string.Equals(e, game.Origin, StringComparison.OrdinalIgnoreCase));
                    settings.Selected.Remove(game.Key);
                    games.RemoveAll(g => g.Key == game.Key);
                    break;
                case "Folder":
                    if (MessageBox.Show(this, T("dlg.removeFolder", game.Origin), T("btn.remove"), MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                    settings.CustomFolders.RemoveAll(f => string.Equals(f, game.Origin, StringComparison.OrdinalIgnoreCase));
                    foreach (Game g in games.Where(g => g.Origin == game.Origin)) settings.Selected.Remove(g.Key);
                    games.RemoveAll(g => g.Origin == game.Origin);
                    break;
                case "Missing":
                    settings.Selected.Remove(game.Key);
                    games.RemoveAll(g => g.Key == game.Key);
                    break;
                default: return;
            }
            Save();
            UpdateProfileList();
            ShowGames();
        }

        void StartGame(Game game)
        {
            string target = game?.Launch ?? game?.MainExe;
            if (string.IsNullOrEmpty(target)) return;
            try
            {
                var psi = new ProcessStartInfo(target) { UseShellExecute = true };
                if (File.Exists(target)) psi.WorkingDirectory = Path.GetDirectoryName(target);
                Process.Start(psi);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "GameSwitcher"); }
        }

        void OpenSelectedLocation()
        {
            Game game = SelectedGame;
            if (game == null) return;
            if (File.Exists(game.Path)) Process.Start("explorer.exe", $"/select,\"{game.Path}\"");
            else if (Directory.Exists(game.Path)) Process.Start("explorer.exe", $"\"{game.Path}\"");
        }

        void EditSelectedProfile()
        {
            Game game = SelectedGame;
            if (game == null) return;
            if (!settings.Selected.ContainsKey(game.Key)) list.SelectedItems[0].Checked = true;
            SelectProfile(game.Key);
            if (!settings.Profiles.ContainsKey(game.Key)) chkOwnProfile.Checked = true;
        }

        void AddGame(Game game)
        {
            games.RemoveAll(g => g.Key == game.Key);
            games.Add(game);
            games = games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        #endregion

        #region Profiles and actions

        // The action set shown in the panel and whether it can be edited
        (ActionSet Actions, bool Editable) EditActions =>
            editKey.Length == 0 ? (settings.Actions, true)
            : settings.Profiles.TryGetValue(editKey, out ActionSet own) ? (own, true)
            : (settings.Actions, false);   // game without own profile: shows the default, read-only

        void UpdateProfileList()
        {
            loading = true;
            cmbProfile.Items.Clear();
            var current = new Item { Code = "", Name = T("profile.default") };
            cmbProfile.Items.Add(current);
            foreach (var s in settings.Selected.Values.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var i = new Item { Code = s.Key, Name = s.Name };
                cmbProfile.Items.Add(i);
                if (s.Key == editKey) current = i;
            }
            cmbProfile.SelectedItem = current;
            editKey = current.Code;
            loading = false;
            ShowActions();
        }

        void SelectProfile(string key)
        {
            foreach (Item i in cmbProfile.Items) if (i.Code == key) { cmbProfile.SelectedItem = i; return; }
            cmbProfile.SelectedIndex = 0;
        }

        static Item AppEntry(string path)
        {
            string desc = null;
            try { desc = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); } catch { }
            string file = Path.GetFileName(path);
            return new Item { Code = path, Name = string.IsNullOrEmpty(desc) ? file : $"{desc}  ({file})" };
        }

        // Fills a device combo box and selects the stored device (shown as "not connected" if missing)
        void FillDeviceCombo(ComboBox combo, int flow, Dictionary<string, object> action)
        {
            combo.Items.Clear();
            List<Item> devices;
            if (demo)
                devices = (flow == Audio.Output ? new[] { "Headset (USB Audio)", "Monitor (HDMI Audio)", "Speakers (Realtek Audio)" } : new[] { "Headset Microphone (USB Audio)", "Microphone (Webcam)" })
                    .Select(n => new Item { Code = n.StartsWith("Headset (") ? "demo-headset" : n, Name = n }).ToList();
            else
                devices = (SafeGet(() => Audio.GetDevices(flow)) ?? new List<AudioDevice>()).Select(d => new Item { Code = d.Id, Name = d.Name }).ToList();
            foreach (var d in devices) combo.Items.Add(d);
            string id = action.Str("DeviceId");
            if (id.Length == 0) return;
            Item match = devices.FirstOrDefault(d => d.Code == id);
            if (match == null)
            {
                match = new Item { Code = id, Name = T("audio.disconnected", action.Str("DeviceName")), Disconnected = true };
                combo.Items.Add(match);
            }
            combo.SelectedItem = match;
        }

        void FillModeCombo(Dictionary<string, object> action)
        {
            cmbDisplay.Items.Clear();
            var modes = SafeGet(DisplayModes.All) ?? new List<DisplayMode>();
            foreach (var m in modes) cmbDisplay.Items.Add(m);
            int w = action.Int("Width"), h = action.Int("Height"), hz = action.Int("Hz");
            if (w == 0) return;
            DisplayMode match = modes.FirstOrDefault(m => m.Width == w && m.Height == h && m.Hz == hz);
            if (match == null) { match = new DisplayMode { Width = w, Height = h, Hz = hz }; cmbDisplay.Items.Add(match); }
            cmbDisplay.SelectedItem = match;
        }

        void FillPowerCombo(Dictionary<string, object> action)
        {
            cmbPower.Items.Clear();
            Item current = null;
            foreach (var p in SafeGet(PowerPlans.List) ?? new List<PowerPlan>())
            {
                var i = new Item { Code = p.Id, Name = p.Name };
                cmbPower.Items.Add(i);
                if (p.Id == action.Str("PlanId")) current = i;
            }
            if (current != null) cmbPower.SelectedItem = current;
            else if (demo && cmbPower.Items.Count > 0) cmbPower.SelectedIndex = Math.Min(1, cmbPower.Items.Count - 1);
        }

        void UpdateActionControls()
        {
            bool editable = EditActions.Editable;
            chkOwnProfile.Visible = editKey.Length > 0;
            foreach (var b in bindings) b.Control.Enabled = editable;
            foreach (var (master, deps) in dependents)
                foreach (Control c in deps)
                    // selection fields stay usable (disabled ones render poorly in the dark design)
                    c.Enabled = editable && (c is ComboBox || c is NumericUpDown || master.Checked);
            if (!nightLightSupported) chkNight.Enabled = chkNightRevert.Enabled = false;
        }

        void ShowActions()
        {
            var (a, _) = EditActions;
            loading = true;
            chkOwnProfile.Checked = editKey.Length > 0 && settings.Profiles.ContainsKey(editKey);
            foreach (var (control, action, field) in bindings)
            {
                object value = a[action].Get(field);
                switch (control)
                {
                    case CheckBox cb: cb.Checked = value is bool b && b; break;
                    case NumericUpDown n: n.Value = Math.Max(0, Math.Min(100, a[action].Int(field))); break;
                    case TextBox t: t.Text = value as string ?? ""; break;
                }
            }
            FillDeviceCombo(cmbAudio, Audio.Output, a["Audio"]);
            FillDeviceCombo(cmbMic, Audio.Input, a["Mic"]);
            FillModeCombo(a["Display"]);
            FillPowerCombo(a["Power"]);
            lstClose.Items.Clear(); foreach (string p in a["CloseApps"].Strings("Apps")) lstClose.Items.Add(AppEntry(p));
            lstStart.Items.Clear(); foreach (string p in a["StartApps"].Strings("Apps")) lstStart.Items.Add(AppEntry(p));
            loading = false;
            UpdateActionControls();
        }

        void SaveActions()
        {
            if (loading) return;
            var (a, editable) = EditActions;
            if (!editable) return;
            foreach (var (control, action, field) in bindings)
                a[action][field] = control is CheckBox cb ? cb.Checked
                                 : control is NumericUpDown n ? (int)n.Value
                                 : (object)((TextBox)control).Text;
            foreach (var (combo, action) in new[] { (cmbAudio, "Audio"), (cmbMic, "Mic") })
                if (combo.SelectedItem is Item dev)
                {
                    a[action]["DeviceId"] = dev.Code;
                    if (!dev.Disconnected) a[action]["DeviceName"] = dev.Name;
                }
            if (cmbDisplay.SelectedItem is DisplayMode m) { a["Display"]["Width"] = m.Width; a["Display"]["Height"] = m.Height; a["Display"]["Hz"] = m.Hz; }
            if (cmbPower.SelectedItem is Item plan) { a["Power"]["PlanId"] = plan.Code; a["Power"]["PlanName"] = plan.Name; }
            a["CloseApps"]["Apps"] = lstClose.Items.Cast<Item>().Select(i => i.Code).ToArray();
            a["StartApps"]["Apps"] = lstStart.Items.Cast<Item>().Select(i => i.Code).ToArray();
            UpdateActionControls();
            Save();
        }

        void AddAppToList(ListBox target, string path)
        {
            if (target == null || string.IsNullOrEmpty(path)) return;
            if (target.Items.Cast<Item>().Any(i => string.Equals(i.Code, path, StringComparison.OrdinalIgnoreCase))) return;
            target.Items.Add(AppEntry(path));
            SaveActions();
        }

        #endregion

        #region Events

        void WireEvents()
        {
            list.ItemChecked += (s, e) =>
            {
                if (loading) return;
                var game = (Game)e.Item.Tag;
                if (e.Item.Checked) settings.Selected[game.Key] = new GameRef { Key = game.Key, Name = game.Name, Exes = game.Exes };
                else settings.Selected.Remove(game.Key);
                e.Item.SubItems[2].Text = ProfileText(game);
                Save();
                UpdateProfileList();
                UpdateStatus();
                UpdateRunning();
            };
            list.SelectedIndexChanged += (s, e) =>
            {
                UpdateRemoveButton();
                Game game = SelectedGame;
                if (game != null) SelectProfile(settings.Selected.ContainsKey(game.Key) ? game.Key : "");
            };
            list.ColumnClick += (s, e) =>
            {
                if (sorter.Column == e.Column) sorter.Descending = !sorter.Descending;
                else { sorter.Column = e.Column; sorter.Descending = e.Column == 3 || e.Column == 4; }   // play time and date: largest first
                list.Sort();
            };
            list.DoubleClick += (s, e) => StartGame(SelectedGame);
            menu.Opening += (s, e) =>
            {
                Game game = SelectedGame;
                if (game == null) { e.Cancel = true; return; }
                miLaunch.Enabled = !string.IsNullOrEmpty(game.Launch ?? game.MainExe);
                miOpen.Enabled = File.Exists(game.Path) || Directory.Exists(game.Path);
                miRemove.Enabled = RemovableSources.Contains(game.Source);
            };

            txtSearch.TextChanged += (s, e) => ShowGames();
            cmbSource.SelectedIndexChanged += (s, e) => { if (!loading) ShowGames(); };
            chkOnlySelected.CheckedChanged += (s, e) => ShowGames();
            btnRescan.Click += (s, e) => Scan();
            btnRemove.Click += (s, e) => RemoveSelectedGame();
            btnSettings.Click += (s, e) => { using (var dlg = new SettingsDialog(this, settings)) dlg.ShowDialog(this); };
            btnLog.Click += (s, e) =>
            {
                string log = Paths.Data("watcher.log");
                if (File.Exists(log)) Process.Start("notepad.exe", $"\"{log}\"");
                else MessageBox.Show(this, T("dlg.noLog"), T("btn.log"));
            };
            lnkUpdate.LinkClicked += (s, e) => Process.Start($"https://github.com/{AppInfo.Repo}/releases/latest");

            btnToggleHdr.Click += (s, e) =>
            {
                bool enable = !hdrDisplays.Any(d => d.HdrEnabled);
                try { foreach (var d in hdrDisplays.Where(d => d.HdrEnabled != enable)) DisplayConfig.SetHdr(d, enable); }
                catch (Exception ex) { MessageBox.Show(this, T("dlg.hdrError", ex.Message), "HDR"); }
                System.Threading.Thread.Sleep(700);
                UpdateHdr();
            };
            btnPause.Click += (s, e) =>
            {
                if (!demo && Pause.Get() != null) { Pause.Clear(); UpdatePauseLabel(); }
                else pauseMenu.Show(btnPause, 0, btnPause.Height);
            };

            cmbProfile.SelectedIndexChanged += (s, e) =>
            {
                if (loading || !(cmbProfile.SelectedItem is Item i)) return;
                editKey = i.Code;
                ShowActions();
            };
            chkOwnProfile.CheckedChanged += (s, e) =>
            {
                if (loading || editKey.Length == 0) return;
                if (chkOwnProfile.Checked) settings.Profiles[editKey] = settings.Actions.Clone();
                else settings.Profiles.Remove(editKey);
                Save();
                ShowActions();
                foreach (ListViewItem item in list.Items)
                    if (((Game)item.Tag).Key == editKey) item.SubItems[2].Text = ProfileText((Game)item.Tag);
            };

            foreach (var (control, _, _) in bindings)
            {
                if (control is CheckBox cb) cb.CheckedChanged += (s, e) => SaveActions();
                else if (control is NumericUpDown n) n.ValueChanged += (s, e) => SaveActions();
                else control.Leave += (s, e) => SaveActions();
            }
            foreach (var c in new[] { cmbAudio, cmbMic, cmbDisplay, cmbPower }) c.SelectedIndexChanged += (s, e) => SaveActions();
            cmbAudio.DropDown += (s, e) => { loading = true; FillDeviceCombo(cmbAudio, Audio.Output, EditActions.Actions["Audio"]); loading = false; };
            cmbMic.DropDown += (s, e) => { loading = true; FillDeviceCombo(cmbMic, Audio.Input, EditActions.Actions["Mic"]); loading = false; };

            btnCloseAdd.Click += (s, e) => { addTarget = lstClose; addMenu.Show(btnCloseAdd, 0, btnCloseAdd.Height); };
            btnStartAdd.Click += (s, e) => { addTarget = lstStart; addMenu.Show(btnStartAdd, 0, btnStartAdd.Height); };
            btnCloseRemove.Click += (s, e) => { if (lstClose.SelectedItem != null) { lstClose.Items.Remove(lstClose.SelectedItem); SaveActions(); } };
            btnStartRemove.Click += (s, e) => { if (lstStart.SelectedItem != null) { lstStart.Items.Remove(lstStart.SelectedItem); SaveActions(); } };
            foreach (var (button, text) in new[] { (btnScriptStart, txtScriptStart), (btnScriptEnd, txtScriptEnd) })
                button.Click += (s, e) =>
                {
                    using (var dlg = new OpenFileDialog { Title = T("dlg.chooseScript"), Filter = T("dlg.scriptFilter") })
                        if (dlg.ShowDialog(this) == DialogResult.OK) { text.Text = $"\"{dlg.FileName}\""; SaveActions(); }
                };

            btnAddExe.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Title = T("dlg.chooseGame"), Filter = T("dlg.exeFilter") })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    if (!settings.CustomExes.Contains(dlg.FileName, StringComparer.OrdinalIgnoreCase)) settings.CustomExes.Add(dlg.FileName);
                    Game game = GameLibrary.FromExe(dlg.FileName);
                    settings.Selected[game.Key] = new GameRef { Key = game.Key, Name = game.Name, Exes = game.Exes };
                    AddGame(game);
                    Save();
                    UpdateSourceFilter();
                    UpdateProfileList();
                    ShowGames();
                }
            };
            btnAddFolder.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { Description = T("dlg.folderDescription") })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    var found = GameLibrary.FromGamesFolder(dlg.SelectedPath, "Folder");
                    if (found.Count == 0) { MessageBox.Show(this, T("dlg.noGamesInFolder"), T("btn.addFolder")); return; }
                    if (!settings.CustomFolders.Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase)) settings.CustomFolders.Add(dlg.SelectedPath);
                    foreach (Game g in found) AddGame(g);
                    Save();
                    UpdateSourceFilter();
                    ShowGames();
                    MessageBox.Show(this, T("dlg.gamesFound", found.Count), T("btn.addFolder"));
                }
            };

            // Refresh the display every 2 s, the service state and play time every 10 s
            timer.Tick += (s, e) =>
            {
                UpdateHdr(); UpdateRunning(); UpdateAudioLabel(); UpdatePauseLabel();
                if (++tick % 5 == 0) { UpdateServiceState(); UpdateStatus(); UpdatePlaytimeColumns(); }
            };

            Shown += (s, e) =>
            {
                ApplyTheme();
                UpdateAudioLabel(); UpdateHdr(); UpdatePauseLabel();
                Scan();
                FirstRunSetup();
                UpdateServiceState(); UpdateStatus(); UpdateRunning();
                if (settings.CheckUpdates && !demo) CheckForUpdates(null);
                timer.Start();
            };
            FormClosed += (s, e) => timer.Stop();
        }

        // First start: offer autostart and a Start menu entry, so that unpacking the ZIP and starting
        // GameSwitcher.exe is all it takes. Opening the window always makes sure the service runs.
        void FirstRunSetup()
        {
            if (demo) return;
            if (!settings.SetupDone)
            {
                settings.SetupDone = true;
                if (!Autostart.IsEnabled() &&
                    MessageBox.Show(this, T("setup.text"), T("setup.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    try { Autostart.Enable(startNow: false); }
                    catch (Exception ex) { MessageBox.Show(this, T("dlg.autostartError", ex.Message), T("opt.autostart")); }
                }
                Save();
            }
            Autostart.StartService();
        }

        // Checks GitHub for a newer release; label (optional) receives the result text
        public void CheckForUpdates(Label label)
        {
            UpdateChecker.LatestTag(tag => BeginInvoke((Action)(() =>
            {
                Version latest = null;
                if (tag != null) Version.TryParse(tag.TrimStart('v'), out latest);
                bool newer = latest != null && latest > Version.Parse(AppInfo.Version);
                if (newer) { updateTag = tag; lnkUpdate.Text = T("update.available", tag); lnkUpdate.Visible = true; }
                if (label != null && !label.IsDisposed)
                    label.Text = latest == null ? T("update.failed") : newer ? T("update.available", tag) : T("update.none", AppInfo.Version);
            })));
        }

        #endregion

        #region Demo data (screenshots only)

        List<Game> DemoGames()
        {
            var list = new[]
            {
                ("Arcfall Legends", "Steam", "ArcfallLegends.exe"), ("Deep Horizon", "Epic", "DeepHorizon-Win64-Shipping.exe"),
                ("Ironclad Frontier", "Steam", "Ironclad.exe"), ("Kingdoms of Ash", "GOG", "KingdomsOfAsh.exe"),
                ("Mythwood Chronicles", "Steam", "Mythwood.exe"), ("Neon Drift", "Xbox", "NeonDrift.exe"),
                ("Pixel Brawlers", "Steam", "PixelBrawlers.exe"), ("Shadowline", "Ubisoft", "Shadowline.exe"),
                ("Starfall Tactics", "EA", "StarfallTactics.exe"), ("Velocity Rush", "Epic", "VelocityRush.exe")
            };
            return list.Select(g => new Game { Name = g.Item1, Source = g.Item2, Path = "", Key = "demo:" + g.Item1, Exes = new[] { g.Item3 } }).ToList();
        }

        public static GuiSettings DemoSettings(out Dictionary<string, Dictionary<string, object>> playtime)
        {
            var s = new GuiSettings { Demo = true, SetupDone = true, CheckUpdates = false };
            var loaded = GuiSettings.Load();
            s.Language = loaded.Language; s.Theme = loaded.Theme;
            foreach (string n in new[] { "Arcfall Legends", "Deep Horizon", "Mythwood Chronicles", "Neon Drift", "Velocity Rush" })
                s.Selected["demo:" + n] = new GameRef { Key = "demo:" + n, Name = n, Exes = new string[0] };
            s.Profiles["demo:Neon Drift"] = new ActionSet(null);
            s.Actions["Audio"]["Enabled"] = true; s.Actions["Audio"]["DeviceId"] = "demo-headset"; s.Actions["Audio"]["DeviceName"] = "Headset (USB Audio)";
            s.Actions["Power"]["Enabled"] = true;
            s.Actions["KeepAwake"]["Enabled"] = true;
            playtime = new Dictionary<string, Dictionary<string, object>>();
            foreach (var (name, secs, daysAgo) in new[] { ("Arcfall Legends", 152000, 0), ("Deep Horizon", 48200, 2), ("Mythwood Chronicles", 391000, 1), ("Neon Drift", 12600, 5), ("Velocity Rush", 2700, 9) })
                playtime["demo:" + name] = new Dictionary<string, object> { ["Name"] = name, ["Seconds"] = secs, ["LastPlayed"] = DateTime.Now.AddDays(-daysAgo).ToString("o") };
            return s;
        }

        public void SetDemoPlaytime(Dictionary<string, Dictionary<string, object>> playtime) => demoPlaytime = playtime;

        #endregion
    }
}
