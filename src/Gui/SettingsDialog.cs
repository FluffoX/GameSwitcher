using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using GameSwitcher.Native;
using static GameSwitcher.Strings;

namespace GameSwitcher.Gui
{
    // Left: autostart, service, language, design, notifications, tray, automatic detection, hotkey, updates.
    // Right: optional extras (session summary, recently played, batteries, couch mode).
    class SettingsDialog : Form
    {
        readonly MainForm main;
        readonly GuiSettings settings;
        bool loading;
        readonly int column;

        readonly Label secGeneral = NewSection(new Padding(3, 4, 3, 2)), secComfort = NewSection(new Padding(3, 4, 3, 2));
        readonly Label secBattery = NewSection(new Padding(3, 16, 3, 2)), secCouch = NewSection(new Padding(3, 16, 3, 2));
        readonly CheckBox chkAutostart = NewCheck(new Padding(3, 6, 3, 3));
        readonly Label lblService = NewLabel(new Padding(23, 8, 8, 3));
        readonly Button btnService;
        readonly Label lblLanguage = NewLabel(new Padding(3, 14, 8, 3)), lblTheme = NewLabel(new Padding(3, 8, 8, 3));
        readonly ComboBox cmbLanguage, cmbTheme;
        readonly CheckBox chkNotify = NewCheck(new Padding(3, 14, 3, 3)), chkAutoDetect = NewCheck(new Padding(3, 6, 3, 3));
        readonly CheckBox chkTrayHdr = NewCheck(new Padding(3, 6, 3, 3));
        readonly CheckBox chkHotkey = NewCheck(new Padding(3, 10, 3, 3));
        readonly TextBox txtHotkey = new TextBox { ReadOnly = true, Margin = new Padding(6, 8, 3, 3) };
        readonly Label lblHotkeyHint = NewHint(new Padding(23, 0, 3, 3));
        readonly CheckBox chkUpdates = NewCheck(new Padding(3, 18, 3, 3));
        readonly Button btnCheck, btnClose;
        readonly Label lblUpdate = NewLabel(new Padding(23, 2, 3, 3));
        readonly LinkLabel lnkAbout = new LinkLabel { AutoSize = true, Margin = new Padding(3, 16, 3, 3) };

        readonly CheckBox chkSummary = NewCheck(new Padding(3, 6, 3, 3)), chkRecent = NewCheck(new Padding(3, 6, 3, 3));
        readonly CheckBox chkBattery = NewCheck(new Padding(3, 6, 3, 3)), chkBatteryWarn = NewCheck(new Padding(23, 6, 3, 3)), chkBatteryStart = NewCheck(new Padding(23, 4, 3, 3));
        readonly NumericUpDown numThreshold = new NumericUpDown { Minimum = 5, Maximum = 90, Margin = new Padding(3, 4, 3, 3) };
        readonly Label lblPercent = NewLabel(new Padding(0, 8, 3, 3)), lblBatteryHint = NewHint(new Padding(23, 2, 3, 3));
        readonly CheckBox chkCouch = NewCheck(new Padding(3, 6, 3, 3)), chkCouchRevert = NewCheck(new Padding(23, 6, 3, 3));
        readonly Label lblCouchHint = NewHint(new Padding(23, 0, 3, 3));
        readonly Label lblCouchDisplay = NewLabel(new Padding(23, 9, 8, 3)), lblCouchAudio = NewLabel(new Padding(23, 9, 8, 3)), lblCouchCommand = NewLabel(new Padding(23, 9, 8, 3));
        readonly ComboBox cmbCouchDisplay, cmbCouchAudio;
        readonly TextBox txtCouchCommand = new TextBox { Margin = new Padding(3, 6, 3, 3) };

        static CheckBox NewCheck(Padding margin) => new CheckBox { AutoSize = true, Margin = margin };
        static Label NewLabel(Padding margin) => new Label { AutoSize = true, Margin = margin };
        static Label NewHint(Padding margin) => new Label { AutoSize = true, Margin = margin, Tag = "hint" };
        static Label NewSection(Padding margin) => new Label { AutoSize = true, Margin = margin };

        Button NewButton(Padding margin) => new Button
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 0, 6, 0), Margin = margin,
            MinimumSize = new Size(LogicalToDeviceUnits(80), LogicalToDeviceUnits(26))
        };

        static FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            row.Controls.AddRange(controls);
            return row;
        }

        FlowLayoutPanel Column(params Control[] controls)
        {
            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 0, LogicalToDeviceUnits(18), 0),
                MinimumSize = new Size(column, 0)
            };
            panel.Controls.AddRange(controls);
            return panel;
        }

        public SettingsDialog(MainForm main, GuiSettings settings)
        {
            this.main = main;
            this.settings = settings;
            Font = main.Font; Icon = main.Icon;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MinimizeBox = MaximizeBox = false; ShowInTaskbar = false;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            column = LogicalToDeviceUnits(440);

            btnService = NewButton(new Padding(3));
            btnCheck = NewButton(new Padding(12, 12, 3, 3));
            btnClose = NewButton(new Padding(3, 16, 3, 3)); btnClose.DialogResult = DialogResult.OK;
            AcceptButton = CancelButton = btnClose;
            cmbLanguage = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = LogicalToDeviceUnits(200), Margin = new Padding(3, 10, 3, 3) };
            cmbTheme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = LogicalToDeviceUnits(200), Margin = new Padding(3, 4, 3, 3) };
            cmbCouchDisplay = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = LogicalToDeviceUnits(240), Margin = new Padding(3, 6, 3, 3) };
            cmbCouchAudio = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = LogicalToDeviceUnits(240), Margin = new Padding(3, 6, 3, 3) };
            txtCouchCommand.Width = LogicalToDeviceUnits(240);
            txtHotkey.Width = LogicalToDeviceUnits(130);
            numThreshold.Width = LogicalToDeviceUnits(55);
            int wide = column - LogicalToDeviceUnits(10), indented = column - LogicalToDeviceUnits(30);
            foreach (var c in new[] { chkAutoDetect, chkTrayHdr, chkNotify, chkSummary, chkRecent, chkBattery, chkCouch }) c.Tag = wide;
            foreach (var c in new[] { chkBatteryStart, chkCouchRevert }) c.Tag = indented;
            chkBatteryWarn.Tag = indented - LogicalToDeviceUnits(80);
            foreach (var l in new[] { lblHotkeyHint, lblBatteryHint, lblCouchHint }) l.MaximumSize = new Size(indented, 0);

            var left = Column(secGeneral, chkAutostart, Row(lblService, btnService), Row(lblLanguage, cmbLanguage), Row(lblTheme, cmbTheme),
                chkNotify, chkTrayHdr, chkAutoDetect, Row(chkHotkey, txtHotkey), lblHotkeyHint,
                Row(chkUpdates, btnCheck), lblUpdate, lnkAbout);
            var right = Column(secComfort, chkSummary, chkRecent,
                secBattery, chkBattery, Row(chkBatteryWarn, numThreshold, lblPercent), chkBatteryStart, lblBatteryHint,
                secCouch, chkCouch, lblCouchHint, Row(lblCouchDisplay, cmbCouchDisplay), Row(lblCouchAudio, cmbCouchAudio),
                Row(lblCouchCommand, txtCouchCommand), chkCouchRevert);
            var columns = Row(left, right);
            var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(14, 10, 14, 10) };
            panel.Controls.AddRange(new Control[] { columns, btnClose });
            Controls.Add(panel);

            // Initial values
            loading = true;
            chkAutostart.Checked = Autostart.IsEnabled();
            foreach (var l in Languages())
            {
                var item = new Item { Code = l.Key, Name = l.Value };
                cmbLanguage.Items.Add(item);
                if (l.Key == Code) cmbLanguage.SelectedItem = item;
            }
            chkNotify.Checked = settings.Notifications;
            chkAutoDetect.Checked = settings.AutoDetect;
            chkTrayHdr.Checked = settings.TrayHdrToggle;
            chkHotkey.Checked = settings.Hotkey.Length > 0;
            txtHotkey.Text = settings.Hotkey.Length > 0 ? settings.Hotkey : "Ctrl+Alt+G";
            txtHotkey.Enabled = chkHotkey.Checked;
            chkUpdates.Checked = settings.CheckUpdates;
            chkSummary.Checked = settings.SessionSummary;
            chkRecent.Checked = settings.RecentInTray;
            chkBattery.Checked = settings.Battery;
            chkBatteryWarn.Checked = settings.BatteryWarn;
            chkBatteryStart.Checked = settings.BatteryGameStart;
            numThreshold.Value = Math.Max(5, Math.Min(90, settings.BatteryThreshold));
            chkCouch.Checked = settings.Couch.Enabled;
            chkCouchRevert.Checked = settings.Couch.Revert;
            txtCouchCommand.Text = settings.Couch.Command;
            FillCouchAudio();
            loading = false;
            SetTexts();
            UpdateEnabled();
            WireEvents();
            Shown += (s, e) => Theme.Apply(this, Theme.IsDark(settings.Theme), new ContextMenuStrip[0]);
        }

        void FillCouchAudio()
        {
            cmbCouchAudio.Items.Clear();
            var none = new Item { Code = "", Name = T("couch.audioKeep") };
            cmbCouchAudio.Items.Add(none);
            cmbCouchAudio.SelectedItem = none;
            List<AudioDevice> devices;
            try { devices = settings.Demo ? new List<AudioDevice>() : Audio.GetDevices(Audio.Output); } catch { devices = new List<AudioDevice>(); }
            foreach (var d in devices)
            {
                var item = new Item { Code = d.Id, Name = d.Name };
                cmbCouchAudio.Items.Add(item);
                if (d.Id == settings.Couch.AudioId) cmbCouchAudio.SelectedItem = item;
            }
            if (settings.Couch.AudioId.Length > 0 && !devices.Any(d => d.Id == settings.Couch.AudioId))
            {
                var missing = new Item { Code = settings.Couch.AudioId, Name = T("audio.disconnected", settings.Couch.AudioName), Disconnected = true };
                cmbCouchAudio.Items.Add(missing);
                cmbCouchAudio.SelectedItem = missing;
            }
        }

        void SetTexts()
        {
            Font = main.Font;
            var bold = new Font(main.Font.FontFamily, 9.5f, FontStyle.Bold);
            foreach (var sec in new[] { secGeneral, secComfort, secBattery, secCouch }) sec.Font = bold;
            Text = T("settings.title");
            secGeneral.Text = T("settings.general");
            secComfort.Text = T("settings.comfort");
            secBattery.Text = T("settings.battery");
            secCouch.Text = T("settings.couch");
            chkAutostart.Text = T("opt.autostart");
            lblService.Text = T("settings.service") + " " + (main.ServiceIsRunning ? T("service.running") : T("service.stopped"));
            btnService.Text = main.ServiceIsRunning ? T("btn.serviceStop") : T("btn.serviceStart");
            lblLanguage.Text = T("language.label");
            lblTheme.Text = T("opt.theme");
            chkNotify.Text = T("opt.notifications");
            chkAutoDetect.Text = T("opt.autoDetect");
            chkTrayHdr.Text = T("opt.trayHdr");
            chkHotkey.Text = T("opt.hotkey");
            lblHotkeyHint.Text = T("hotkey.hint");
            chkUpdates.Text = T("opt.updates");
            btnCheck.Text = T("btn.checkUpdates");
            lnkAbout.Text = T("about.version", AppInfo.Version) + "   ·   github.com/" + AppInfo.Repo;
            btnClose.Text = T("btn.close");
            chkSummary.Text = T("opt.sessionSummary");
            chkRecent.Text = T("opt.recentGames");
            chkBattery.Text = T("opt.battery");
            chkBatteryWarn.Text = T("opt.batteryWarn");
            lblPercent.Text = "%";
            chkBatteryStart.Text = T("opt.batteryGameStart");
            lblBatteryHint.Text = T("battery.hint");
            chkCouch.Text = T("opt.couch");
            lblCouchHint.Text = T("couch.hint");
            lblCouchDisplay.Text = T("couch.display");
            lblCouchAudio.Text = T("couch.audio");
            lblCouchCommand.Text = T("couch.command");
            chkCouchRevert.Text = T("couch.revert");
            loading = true;
            cmbTheme.Items.Clear();
            foreach (string t in new[] { "system", "light", "dark" })
            {
                var item = new Item { Code = t, Name = T("theme." + t) };
                cmbTheme.Items.Add(item);
                if (t == settings.Theme) cmbTheme.SelectedItem = item;
            }
            cmbCouchDisplay.Items.Clear();
            foreach (string d in new[] { "external", "clone", "extend", "keep" })
            {
                var item = new Item { Code = d, Name = T("couch.display." + d) };
                cmbCouchDisplay.Items.Add(item);
                if (d == settings.Couch.Display) cmbCouchDisplay.SelectedItem = item;
            }
            if (cmbCouchDisplay.SelectedIndex < 0) cmbCouchDisplay.SelectedIndex = 0;
            FillCouchAudio();
            loading = false;
            Theme.FitCheckBoxes(this);
        }

        // Sub-options are only usable while their main option is on
        void UpdateEnabled()
        {
            // selection fields stay usable (disabled ones render poorly in the dark design)
            chkBatteryWarn.Enabled = chkBatteryStart.Enabled = chkBattery.Checked;
            chkCouchRevert.Enabled = chkCouch.Checked;
        }

        // "Ctrl+Alt+G" from a key press; a hotkey needs at least one modifier
        static string FormatHotkey(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin) return null;
            var parts = new List<string>();
            if (e.Control) parts.Add("Ctrl");
            if (e.Alt) parts.Add("Alt");
            if (e.Shift) parts.Add("Shift");
            if (parts.Count == 0) return null;
            parts.Add(e.KeyCode.ToString());
            return string.Join("+", parts);
        }

        void Changed(Action apply)
        {
            if (loading) return;
            apply();
            UpdateEnabled();
            main.Save();
        }

        void WireEvents()
        {
            chkAutostart.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                try
                {
                    if (chkAutostart.Checked) Autostart.Enable(startNow: false);
                    else Autostart.Disable();
                }
                catch (Exception ex) { MessageBox.Show(this, T("dlg.autostartError", ex.Message), T("opt.autostart")); }
            };
            btnService.Click += (s, e) =>
            {
                Cursor = Cursors.WaitCursor;
                if (Autostart.ServiceRunning()) Autostart.StopService();
                else { Autostart.StartService(); System.Threading.Thread.Sleep(1500); }
                main.UpdateServiceState();
                Cursor = Cursors.Default;
                SetTexts();
            };
            cmbLanguage.SelectedIndexChanged += (s, e) =>
            {
                if (loading || !(cmbLanguage.SelectedItem is Item i)) return;
                main.ChangeLanguage(i.Code);
                SetTexts();
                Theme.Apply(this, Theme.IsDark(settings.Theme), new ContextMenuStrip[0]);
            };
            cmbTheme.SelectedIndexChanged += (s, e) =>
            {
                if (loading || !(cmbTheme.SelectedItem is Item i)) return;
                main.ChangeTheme(i.Code);
                Theme.Apply(this, Theme.IsDark(settings.Theme), new ContextMenuStrip[0]);
            };
            chkNotify.CheckedChanged += (s, e) => Changed(() => settings.Notifications = chkNotify.Checked);
            chkAutoDetect.CheckedChanged += (s, e) => Changed(() => settings.AutoDetect = chkAutoDetect.Checked);
            chkTrayHdr.CheckedChanged += (s, e) => Changed(() => settings.TrayHdrToggle = chkTrayHdr.Checked);
            chkHotkey.CheckedChanged += (s, e) => Changed(() =>
            {
                txtHotkey.Enabled = chkHotkey.Checked;
                settings.Hotkey = chkHotkey.Checked ? txtHotkey.Text : "";
            });
            txtHotkey.KeyDown += (s, e) =>
            {
                e.SuppressKeyPress = true;
                string text = FormatHotkey(e);
                if (text == null) return;
                txtHotkey.Text = text;
                settings.Hotkey = text;
                main.Save();
            };
            chkUpdates.CheckedChanged += (s, e) => Changed(() => settings.CheckUpdates = chkUpdates.Checked);
            btnCheck.Click += (s, e) => { lblUpdate.Text = T("update.checking"); main.CheckForUpdates(lblUpdate); };
            lnkAbout.LinkClicked += (s, e) => Process.Start("https://github.com/" + AppInfo.Repo);

            chkSummary.CheckedChanged += (s, e) => Changed(() => settings.SessionSummary = chkSummary.Checked);
            chkRecent.CheckedChanged += (s, e) => Changed(() => settings.RecentInTray = chkRecent.Checked);
            chkBattery.CheckedChanged += (s, e) => Changed(() => settings.Battery = chkBattery.Checked);
            chkBatteryWarn.CheckedChanged += (s, e) => Changed(() => settings.BatteryWarn = chkBatteryWarn.Checked);
            chkBatteryStart.CheckedChanged += (s, e) => Changed(() => settings.BatteryGameStart = chkBatteryStart.Checked);
            numThreshold.ValueChanged += (s, e) => Changed(() => settings.BatteryThreshold = (int)numThreshold.Value);
            chkCouch.CheckedChanged += (s, e) => Changed(() => settings.Couch.Enabled = chkCouch.Checked);
            chkCouchRevert.CheckedChanged += (s, e) => Changed(() => settings.Couch.Revert = chkCouchRevert.Checked);
            cmbCouchDisplay.SelectedIndexChanged += (s, e) => Changed(() => settings.Couch.Display = ((Item)cmbCouchDisplay.SelectedItem).Code);
            cmbCouchAudio.SelectedIndexChanged += (s, e) => Changed(() =>
            {
                var i = (Item)cmbCouchAudio.SelectedItem;
                settings.Couch.AudioId = i.Code;
                if (!i.Disconnected) settings.Couch.AudioName = i.Code.Length > 0 ? i.Name : "";
            });
            cmbCouchAudio.DropDown += (s, e) => { loading = true; FillCouchAudio(); loading = false; };
            txtCouchCommand.Leave += (s, e) => Changed(() => settings.Couch.Command = txtCouchCommand.Text.Trim());
            FormClosing += (s, e) => { if (txtCouchCommand.Text.Trim() != settings.Couch.Command) { settings.Couch.Command = txtCouchCommand.Text.Trim(); main.Save(); } };
        }
    }
}
