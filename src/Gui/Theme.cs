using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GameSwitcher.Gui
{
    // Light / dark design for WinForms windows, including title bar, list headers and scrollbars
    static class Theme
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string idList);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        // Undocumented but widely used (Windows 10 1903+): lets "DarkMode_*" control themes render dark
        [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#133")] static extern bool AllowDarkModeForWindow(IntPtr h, bool allow);

        public static void AllowDarkMode() { try { SetPreferredAppMode(1); } catch { } }

        public static bool IsDark(string setting)
        {
            if (setting == "dark") return true;
            if (setting == "light") return false;
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int light && light == 0;
        }

        static void SetDarkControl(IntPtr h, bool dark, string darkTheme, string lightTheme)
        {
            try { AllowDarkModeForWindow(h, dark); } catch { }
            SetWindowTheme(h, dark ? darkTheme : lightTheme, null);
            SendMessage(h, 0x031A, IntPtr.Zero, IntPtr.Zero);   // WM_THEMECHANGED
        }

        public static void Apply(Form window, bool dark, IEnumerable<ContextMenuStrip> menus, Control keepForeColor = null)
        {
            Color back = dark ? Color.FromArgb(32, 32, 32) : SystemColors.Control;
            Color input = dark ? Color.FromArgb(45, 45, 45) : SystemColors.Window;
            Color text = dark ? Color.FromArgb(235, 235, 235) : SystemColors.ControlText;
            Color button = Color.FromArgb(55, 55, 55);

            void ApplyTo(Control parent)
            {
                foreach (Control c in parent.Controls)
                {
                    switch (c)
                    {
                        case Button b:
                            if (dark) { b.FlatStyle = FlatStyle.Flat; b.BackColor = button; b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90); }
                            else { b.FlatStyle = FlatStyle.Standard; b.BackColor = SystemColors.Control; b.UseVisualStyleBackColor = true; }
                            b.ForeColor = text;
                            break;
                        case ComboBox cb:
                            cb.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                            cb.BackColor = input; cb.ForeColor = text;
                            break;
                        case TextBox _: case ListBox _: case ListView _: case NumericUpDown _:
                            c.BackColor = input; c.ForeColor = text;
                            break;
                        case LinkLabel l:
                            l.LinkColor = dark ? Color.FromArgb(110, 170, 255) : Color.FromArgb(0, 102, 204);
                            break;
                        default:
                            c.BackColor = back;
                            if (c.Tag is string hint && hint == "hint") c.ForeColor = dark ? DarkGray : Color.FromArgb(100, 100, 100);
                            else if (c != keepForeColor && !(c.Tag is string tag && tag == "keepColor")) c.ForeColor = text;
                            break;
                    }
                    if ((c is CheckBox || c is Button || c is Label) && Hooked.Add(c)) c.Paint += PaintDisabledText;
                    if (c is ListView lv)
                    {
                        SetDarkControl(lv.Handle, dark, "DarkMode_Explorer", "Explorer");
                        IntPtr header = SendMessage(lv.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);   // LVM_GETHEADER
                        SetDarkControl(header, dark, "DarkMode_ItemsView", "ItemsView");
                        if (Hooked.Add(lv)) DrawHeaderItself(lv);
                    }
                    if (c.Controls.Count > 0) ApplyTo(c);
                }
            }

            window.BackColor = back;
            window.ForeColor = text;
            ApplyTo(window);
            foreach (var m in menus)
            {
                m.BackColor = input;
                foreach (ToolStripItem item in m.Items) { item.ForeColor = text; item.BackColor = input; }
            }
            int v = dark ? 1 : 0;
            DwmSetWindowAttribute(window.Handle, 20, ref v, 4);   // dark title bar
            FitCheckBoxes(window);
        }

        // Light gray for secondary texts in the dark design (hints, "off", disabled options)
        public static readonly Color DarkGray = Color.FromArgb(160, 160, 160);
        static readonly HashSet<Control> Hooked = new HashSet<Control>();

        static bool IsDarkBack(Color c) => c.GetBrightness() < 0.4f;

        // Windows draws the text of disabled checkboxes, buttons and labels in a fixed dark gray that is hardly
        // readable on a dark background: in the dark design it is drawn again in a lighter gray
        static void PaintDisabledText(object sender, PaintEventArgs e)
        {
            var c = (Control)sender;
            if (c.Enabled || !IsDarkBack(c.BackColor) || string.IsNullOrEmpty(c.Text)) return;
            var color = Color.FromArgb(140, 140, 140);
            if (c is CheckBox cb)
            {
                int glyph = CheckBoxRenderer.GetGlyphSize(e.Graphics, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled).Width;
                var area = new Rectangle(glyph + 1, 0, cb.Width - glyph - 1, cb.Height);
                using (var b = new SolidBrush(cb.BackColor)) e.Graphics.FillRectangle(b, area);
                var flags = TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
                int left = glyph + cb.LogicalToDeviceUnits(3);
                Size size = TextRenderer.MeasureText(e.Graphics, cb.Text, cb.Font, new Size(cb.Width - left, 0), flags);
                var textArea = new Rectangle(left, Math.Max(0, (cb.Height - size.Height) / 2), cb.Width - left, size.Height);
                TextRenderer.DrawText(e.Graphics, cb.Text, cb.Font, textArea, color, flags);
            }
            else if (c is Label label)
            {
                using (var b = new SolidBrush(label.BackColor)) e.Graphics.FillRectangle(b, label.ClientRectangle);
                TextRenderer.DrawText(e.Graphics, label.Text, label.Font, label.ClientRectangle, color,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }
            else
            {
                var area = Rectangle.Inflate(c.ClientRectangle, -2, -2);
                using (var b = new SolidBrush(c.BackColor)) e.Graphics.FillRectangle(b, area);
                TextRenderer.DrawText(e.Graphics, c.Text, c.Font, area, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        // The dark list header theme draws its texts too dark: the header is drawn here, the rows by Windows
        static void DrawHeaderItself(ListView lv)
        {
            lv.OwnerDraw = true;
            lv.DrawItem += (s, e) => e.DrawDefault = true;
            lv.DrawSubItem += (s, e) => e.DrawDefault = true;
            lv.DrawColumnHeader += (s, e) =>
            {
                if (!IsDarkBack(lv.BackColor)) { e.DrawDefault = true; return; }
                using (var back = new SolidBrush(Color.FromArgb(40, 40, 40))) e.Graphics.FillRectangle(back, e.Bounds);
                using (var line = new Pen(Color.FromArgb(70, 70, 70)))
                {
                    e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + 3, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
                    e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                }
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                            (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
                var area = Rectangle.Inflate(e.Bounds, -lv.LogicalToDeviceUnits(6), 0);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, lv.Font, area, Color.FromArgb(220, 220, 220), flags);
            };
        }

        // Checkboxes with a width limit (Tag) wrap long texts onto several lines instead of cutting them off
        public static void FitCheckBoxes(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is CheckBox cb && cb.Tag is int max)
                {
                    int box = cb.LogicalToDeviceUnits(22);
                    Size single = TextRenderer.MeasureText(cb.Text, cb.Font);
                    if (single.Width + box <= max) cb.AutoSize = true;
                    else
                    {
                        Size wrapped = TextRenderer.MeasureText(cb.Text, cb.Font, new Size(max - box, 0), TextFormatFlags.WordBreak);
                        cb.AutoSize = false;
                        cb.Size = new Size(max, wrapped.Height + cb.LogicalToDeviceUnits(6));
                    }
                }
                if (c.Controls.Count > 0) FitCheckBoxes(c);
            }
        }
    }
}
