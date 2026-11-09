using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using static GameSwitcher.Strings;

namespace GameSwitcher.Gui
{
    // Lets the user pick one of the running programs (for "close / start programs")
    static class ProcessPicker
    {
        public static string Pick(MainForm owner)
        {
            using (var dlg = new Form
            {
                Text = T("picker.title"), Font = owner.Font, StartPosition = FormStartPosition.CenterParent, Icon = owner.Icon,
                ClientSize = new Size(owner.LogicalToDeviceUnits(620), owner.LogicalToDeviceUnits(480)), MinimizeBox = false, ShowInTaskbar = false
            })
            {
                var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, Sorting = SortOrder.Ascending };
                lv.Columns.Add(T("picker.colProgram"), owner.LogicalToDeviceUnits(250));
                lv.Columns.Add(T("picker.colFile"), owner.LogicalToDeviceUnits(340));
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var programs = Process.GetProcesses().Select(p =>
                {
                    string path = null;
                    try { if (p.Id != Process.GetCurrentProcess().Id) path = p.MainModule.FileName; } catch { }
                    p.Dispose();
                    return path;
                })
                .Where(path => path != null && !path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (string path in programs)
                {
                    string name = null;
                    try { name = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); } catch { }
                    var item = new ListViewItem(string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(path) : name) { Tag = path };
                    item.SubItems.Add(path);
                    lv.Items.Add(item);
                }
                lv.DoubleClick += (s, e) => dlg.DialogResult = DialogResult.OK;

                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
                var cancel = new Button { Text = T("btn.cancel"), AutoSize = true, DialogResult = DialogResult.Cancel, MinimumSize = new Size(owner.LogicalToDeviceUnits(80), 0) };
                var ok = new Button { Text = T("btn.addConfirm"), AutoSize = true, DialogResult = DialogResult.OK, MinimumSize = new Size(owner.LogicalToDeviceUnits(80), 0) };
                buttons.Controls.AddRange(new Control[] { cancel, ok });
                dlg.AcceptButton = ok; dlg.CancelButton = cancel;
                dlg.Controls.AddRange(new Control[] { lv, buttons });
                dlg.Shown += (s, e) => Theme.Apply(dlg, Theme.IsDark(owner.ThemeSetting), new ContextMenuStrip[0]);

                return dlg.ShowDialog(owner) == DialogResult.OK && lv.SelectedItems.Count > 0 ? (string)lv.SelectedItems[0].Tag : null;
            }
        }
    }
}
