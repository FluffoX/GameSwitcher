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
                            if (c != keepForeColor) c.ForeColor = text;
                            break;
                    }
                    if (c is ListView lv)
                    {
                        SetDarkControl(lv.Handle, dark, "DarkMode_Explorer", "Explorer");
                        IntPtr header = SendMessage(lv.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);   // LVM_GETHEADER
                        SetDarkControl(header, dark, "DarkMode_ItemsView", "ItemsView");
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
