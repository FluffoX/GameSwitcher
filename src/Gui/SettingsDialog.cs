using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using static GameSwitcher.Strings;

namespace GameSwitcher.Gui
{
    // Autostart, service, language, design, notifications, automatic detection, hotkey, updates
    class SettingsDialog : Form
    {
        readonly MainForm main;
        readonly GuiSettings settings;
        bool loading;

        readonly CheckBox chkAutostart = NewCheck(new Padding(3, 6, 3, 3));
        readonly Label lblService = NewLabel(new Padding(23, 8, 8, 3));
        readonly Button btnService;
        readonly Label lblLanguage = NewLabel(new Padding(3, 14, 8, 3)), lblTheme = NewLabel(new Padding(3, 8, 8, 3));
        readonly ComboBox cmbLanguage, cmbTheme;
        readonly CheckBox chkNotify = NewCheck(new Padding(3, 14, 3, 3)), chkAutoDetect = NewCheck(new Padding(3, 6, 3, 3));
        readonly CheckBox chkHotkey = NewCheck(new Padding(3, 10, 3, 3));
        readonly TextBox txtHotkey = new TextBox { ReadOnly = true, Margin = new Padding(6, 8, 3, 3) };
        readonly Label lblHotkeyHint = NewLabel(new Padding(23, 0, 3, 3));
        readonly CheckBox chkUpdates = NewCheck(new Padding(3, 18, 3, 3));
        readonly Button btnCheck, btnClose;
        readonly Label lblUpdate = NewLabel(new Padding(23, 2, 3, 3));
        readonly LinkLabel lnkAbout = new LinkLabel { AutoSize = true, Margin = new Padding(3, 16, 3, 3) };

        static CheckBox NewCheck(Padding margin) => new CheckBox { AutoSize = true, Margin = margin };
        static Label NewLabel(Padding margin) => new Label { AutoSize = true, Margin = margin };

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

        public SettingsDialog(MainForm main, GuiSettings settings)
        {
            this.main = main;
            this.settings = settings;
            Font = main.Font; Icon = main.Icon;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MinimizeBox = MaximizeBox = false; ShowInTaskbar = false;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;

            btnService = NewButton(new Padding(3));
            btnCheck = NewButton(new Padding(12, 12, 3, 3));
            btnClose = NewButton(new Padding(3, 16, 3, 3)); btnClose.DialogResult = DialogResult.OK;
            AcceptButton = CancelButton = btnClose;
            cmbLanguage = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = LogicalToDeviceUnits(200), Margin = new Padding(3, 10, 3, 3) };
            cmbTheme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = LogicalToDeviceUnits(200), Margin = new Padding(3, 4, 3, 3) };
            txtHotkey.Width = LogicalToDeviceUnits(130);
            chkAutoDetect.Tag = LogicalToDeviceUnits(500);

            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(14, 10, 14, 10),
                MinimumSize = new Size(LogicalToDeviceUnits(520), 0)
            };
            panel.Controls.AddRange(new Control[] {
                chkAutostart, Row(lblService, btnService), Row(lblLanguage, cmbLanguage), Row(lblTheme, cmbTheme),
                chkNotify, chkAutoDetect, Row(chkHotkey, txtHotkey), lblHotkeyHint,
                Row(chkUpdates, btnCheck), lblUpdate, lnkAbout, btnClose });
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
            chkHotkey.Checked = settings.Hotkey.Length > 0;
            txtHotkey.Text = settings.Hotkey.Length > 0 ? settings.Hotkey : "Ctrl+Alt+G";
            txtHotkey.Enabled = chkHotkey.Checked;
            chkUpdates.Checked = settings.CheckUpdates;
            loading = false;
            SetTexts();
            WireEvents();
            Shown += (s, e) => Theme.Apply(this, Theme.IsDark(settings.Theme), new ContextMenuStrip[0]);
        }

        void SetTexts()
        {
            Font = main.Font;
            Text = T("settings.title");
            chkAutostart.Text = T("opt.autostart");
            lblService.Text = T("settings.service") + " " + (main.ServiceIsRunning ? T("service.running") : T("service.stopped"));
            btnService.Text = main.ServiceIsRunning ? T("btn.serviceStop") : T("btn.serviceStart");
            lblLanguage.Text = T("language.label");
            lblTheme.Text = T("opt.theme");
            chkNotify.Text = T("opt.notifications");
            chkAutoDetect.Text = T("opt.autoDetect");
            chkHotkey.Text = T("opt.hotkey");
            lblHotkeyHint.Text = T("hotkey.hint");
            lblHotkeyHint.ForeColor = Color.Gray;
            chkUpdates.Text = T("opt.updates");
            btnCheck.Text = T("btn.checkUpdates");
            lnkAbout.Text = T("about.version", AppInfo.Version) + "   ·   github.com/" + AppInfo.Repo;
            btnClose.Text = T("btn.close");
            loading = true;
            cmbTheme.Items.Clear();
            foreach (string t in new[] { "system", "light", "dark" })
            {
                var item = new Item { Code = t, Name = T("theme." + t) };
                cmbTheme.Items.Add(item);
                if (t == settings.Theme) cmbTheme.SelectedItem = item;
            }
            loading = false;
            Theme.FitCheckBoxes(this);
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
            chkNotify.CheckedChanged += (s, e) => { if (!loading) { settings.Notifications = chkNotify.Checked; main.Save(); } };
            chkAutoDetect.CheckedChanged += (s, e) => { if (!loading) { settings.AutoDetect = chkAutoDetect.Checked; main.Save(); } };
            chkHotkey.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                txtHotkey.Enabled = chkHotkey.Checked;
                settings.Hotkey = chkHotkey.Checked ? txtHotkey.Text : "";
                main.Save();
            };
            txtHotkey.KeyDown += (s, e) =>
            {
                e.SuppressKeyPress = true;
                string text = FormatHotkey(e);
                if (text == null) return;
                txtHotkey.Text = text;
                settings.Hotkey = text;
                main.Save();
            };
            chkUpdates.CheckedChanged += (s, e) => { if (!loading) { settings.CheckUpdates = chkUpdates.Checked; main.Save(); } };
            btnCheck.Click += (s, e) => { lblUpdate.Text = T("update.checking"); main.CheckForUpdates(lblUpdate); };
            lnkAbout.LinkClicked += (s, e) => Process.Start("https://github.com/" + AppInfo.Repo);
        }
    }
}
