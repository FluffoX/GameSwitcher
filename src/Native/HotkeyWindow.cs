using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GameSwitcher.Native
{
    // Invisible window that receives a global hotkey such as "Ctrl+Alt+G"
    class HotkeyWindow : NativeWindow, IDisposable
    {
        public event EventHandler Pressed;

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
        const int WM_HOTKEY = 0x0312;
        bool registered;

        public HotkeyWindow() { CreateHandle(new CreateParams()); }

        // Returns false if the text is invalid or the combination is already taken
        public bool Register(string text)
        {
            Unregister();
            uint mods = 0, vk = 0;
            foreach (string raw in text.Split('+'))
            {
                string part = raw.Trim();
                switch (part)
                {
                    case "Ctrl": mods |= MOD_CONTROL; break;
                    case "Alt": mods |= MOD_ALT; break;
                    case "Shift": mods |= MOD_SHIFT; break;
                    case "Win": mods |= MOD_WIN; break;
                    default: if (Enum.TryParse(part, true, out Keys key)) vk = (uint)key; break;
                }
            }
            if (vk == 0) return false;
            registered = RegisterHotKey(Handle, 1, mods | MOD_NOREPEAT, vk);
            return registered;
        }

        public void Unregister()
        {
            if (registered) { UnregisterHotKey(Handle, 1); registered = false; }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) Pressed?.Invoke(this, EventArgs.Empty);
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            DestroyHandle();
        }
    }
}
