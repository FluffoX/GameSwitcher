using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameSwitcher.Native
{
    // Mouse and keyboard settings of Windows (Settings → Bluetooth & devices → Mouse / Accessibility)
    static class Input
    {
        [StructLayout(LayoutKind.Sequential)] struct ACCESS_FLAGS { public int cbSize; public int dwFlags; }
        [StructLayout(LayoutKind.Sequential)] struct FILTERKEYS { public int cbSize, dwFlags, iWaitMSec, iDelayMSec, iRepeatMSec, iBounceMSec; }

        [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint param, int[] value, uint winIni);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint param, ref ACCESS_FLAGS value, uint winIni);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint param, ref FILTERKEYS value, uint winIni);

        const uint SPI_GETMOUSE = 0x03, SPI_SETMOUSE = 0x04, SPI_GETMOUSESPEED = 0x70, SPI_SETMOUSESPEED = 0x71;
        const uint SPI_GETFILTERKEYS = 0x32, SPI_SETFILTERKEYS = 0x33, SPI_GETTOGGLEKEYS = 0x34, SPI_SETTOGGLEKEYS = 0x35;
        const uint SPI_GETSTICKYKEYS = 0x3A, SPI_SETSTICKYKEYS = 0x3B;
        const uint SPIF_SAVE = 0x01 | 0x02;   // SPIF_UPDATEINIFILE | SPIF_SENDCHANGE: like the Settings app
        const int HOTKEYACTIVE = 0x04, CONFIRMHOTKEY = 0x08;

        static void Check(bool ok, string what) { if (!ok) throw new Exception(what + " failed: " + Marshal.GetLastWin32Error()); }

        // "Enhance pointer precision": stored as {threshold1, threshold2, acceleration}; acceleration 0 = off
        public static int[] GetMouse()
        {
            var v = new int[3];
            Check(SystemParametersInfo(SPI_GETMOUSE, 0, v, 0), "SPI_GETMOUSE");
            return v;
        }
        public static void SetMouse(int[] v) => Check(SystemParametersInfo(SPI_SETMOUSE, 0, v, SPIF_SAVE), "SPI_SETMOUSE");
        public static bool AccelerationOn() => GetMouse()[2] != 0;

        // Pointer speed 1..20 (10 = Windows default)
        public static int GetSpeed()
        {
            int v = 0;
            Check(SystemParametersInfo(SPI_GETMOUSESPEED, 0, ref v, 0), "SPI_GETMOUSESPEED");
            return v;
        }
        public static void SetSpeed(int speed) =>
            Check(SystemParametersInfo(SPI_SETMOUSESPEED, 0, new IntPtr(Math.Max(1, Math.Min(20, speed))), SPIF_SAVE), "SPI_SETMOUSESPEED");

        // The shortcuts of the accessibility keys (Shift five times for Sticky Keys, holding right Shift
        // for Filter Keys, holding Num Lock for Toggle Keys): returned as one value per feature
        public static int[] GetAccessibilityFlags()
        {
            var sticky = new ACCESS_FLAGS { cbSize = 8 };
            var toggle = new ACCESS_FLAGS { cbSize = 8 };
            var filter = new FILTERKEYS { cbSize = Marshal.SizeOf(typeof(FILTERKEYS)) };
            Check(SystemParametersInfo(SPI_GETSTICKYKEYS, 8, ref sticky, 0), "SPI_GETSTICKYKEYS");
            Check(SystemParametersInfo(SPI_GETTOGGLEKEYS, 8, ref toggle, 0), "SPI_GETTOGGLEKEYS");
            Check(SystemParametersInfo(SPI_GETFILTERKEYS, (uint)filter.cbSize, ref filter, 0), "SPI_GETFILTERKEYS");
            return new[] { sticky.dwFlags, filter.dwFlags, toggle.dwFlags };
        }

        public static void SetAccessibilityFlags(int[] flags)
        {
            var sticky = new ACCESS_FLAGS { cbSize = 8, dwFlags = flags[0] };
            var toggle = new ACCESS_FLAGS { cbSize = 8, dwFlags = flags[2] };
            var filter = new FILTERKEYS { cbSize = Marshal.SizeOf(typeof(FILTERKEYS)) };
            Check(SystemParametersInfo(SPI_GETFILTERKEYS, (uint)filter.cbSize, ref filter, 0), "SPI_GETFILTERKEYS");   // keep the timings
            filter.dwFlags = flags[1];
            Check(SystemParametersInfo(SPI_SETSTICKYKEYS, 8, ref sticky, SPIF_SAVE), "SPI_SETSTICKYKEYS");
            Check(SystemParametersInfo(SPI_SETFILTERKEYS, (uint)filter.cbSize, ref filter, SPIF_SAVE), "SPI_SETFILTERKEYS");
            Check(SystemParametersInfo(SPI_SETTOGGLEKEYS, 8, ref toggle, SPIF_SAVE), "SPI_SETTOGGLEKEYS");
        }

        // The same flags with the shortcuts (and their confirmation) turned off
        public static int[] WithoutShortcuts(int[] flags)
        {
            var r = (int[])flags.Clone();
            for (int i = 0; i < r.Length; i++) r[i] &= ~(HOTKEYACTIVE | CONFIRMHOTKEY);
            return r;
        }
    }

    // Blocks the Windows key while a game window is in the foreground (a low-level keyboard hook;
    // needs a message loop on the thread that installs it). Outside the game the key works as usual.
    sealed class WinKeyBlocker : IDisposable
    {
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

        const int WH_KEYBOARD_LL = 13, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        readonly HookProc proc;   // kept alive as long as the hook is installed
        readonly Func<int, bool> isGameProcess;
        IntPtr hook;

        public WinKeyBlocker(Func<int, bool> isGameProcess)
        {
            this.isGameProcess = isGameProcess;
            proc = Callback;
            using (var me = Process.GetCurrentProcess())
                hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(me.MainModule.ModuleName), 0);
            if (hook == IntPtr.Zero) throw new Exception("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
        }

        IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int vk = Marshal.ReadInt32(lParam);   // KBDLLHOOKSTRUCT.vkCode
                if (vk == VK_LWIN || vk == VK_RWIN)
                {
                    GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                    if (isGameProcess((int)pid)) return new IntPtr(1);   // swallow the key
                }
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
    }
}
