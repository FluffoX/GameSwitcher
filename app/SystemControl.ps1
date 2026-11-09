# System settings used by the actions: display mode (resolution/refresh rate) of the primary display,
# power plans, display standby prevention and night light.
# Run directly:  .\SystemControl.ps1 status
param(
    [ValidateSet('status')]
    [string]$Action
)

if (-not ('SystemNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class DisplayMode {
    public int Width, Height, Hz;
    public override string ToString() { return Width + " × " + Height + " @ " + Hz + " Hz"; }
}

public static class SystemNative {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    const int ENUM_CURRENT_SETTINGS = -1;
    const int DISPLAY_DEVICE_PRIMARY_DEVICE = 4;
    const int DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 1, ES_DISPLAY_REQUIRED = 2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

    public static string PrimaryDevice() {
        DISPLAY_DEVICE dd = new DISPLAY_DEVICE();
        dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++) {
            if ((dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0) return dd.DeviceName;
            dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
        }
        return null;
    }

    static DEVMODE NewDevMode() { DEVMODE dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)); return dm; }

    public static DisplayMode GetCurrentMode() {
        DEVMODE dm = NewDevMode();
        if (!EnumDisplaySettings(PrimaryDevice(), ENUM_CURRENT_SETTINGS, ref dm)) return null;
        DisplayMode m = new DisplayMode(); m.Width = dm.dmPelsWidth; m.Height = dm.dmPelsHeight; m.Hz = dm.dmDisplayFrequency;
        return m;
    }

    // All distinct resolution/refresh combinations of the primary display, largest first
    public static List<DisplayMode> GetModes() {
        string device = PrimaryDevice();
        List<DisplayMode> result = new List<DisplayMode>();
        HashSet<string> seen = new HashSet<string>();
        DEVMODE dm = NewDevMode();
        for (int i = 0; EnumDisplaySettings(device, i, ref dm); i++) {
            if (dm.dmBitsPerPel < 32) continue;
            string key = dm.dmPelsWidth + "x" + dm.dmPelsHeight + "@" + dm.dmDisplayFrequency;
            if (!seen.Add(key)) continue;
            DisplayMode m = new DisplayMode(); m.Width = dm.dmPelsWidth; m.Height = dm.dmPelsHeight; m.Hz = dm.dmDisplayFrequency;
            result.Add(m);
        }
        result.Sort((a, b) => a.Width != b.Width ? b.Width - a.Width : a.Height != b.Height ? b.Height - a.Height : b.Hz - a.Hz);
        return result;
    }

    // Changes the mode dynamically (not saved permanently, so a restart always brings back the normal mode)
    public static int SetMode(int width, int height, int hz) {
        string device = PrimaryDevice();
        DEVMODE dm = NewDevMode();
        if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm)) return -1;
        dm.dmPelsWidth = width; dm.dmPelsHeight = height; dm.dmDisplayFrequency = hz;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        return ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
    }

    // Must be called from a thread that stays alive while the game runs (the watcher's UI thread)
    public static void KeepAwake(bool on) {
        SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);
    }
}
'@
}

#region Power plans

function Get-PowerPlans {
    foreach ($line in (powercfg /list)) {
        if ($line -match '([0-9a-fA-F]{8}-[0-9a-fA-F-]{27})\s+\((.+?)\)(\s*\*)?') {
            [pscustomobject]@{ Id = $Matches[1]; Name = $Matches[2]; Active = [bool]$Matches[3] }
        }
    }
}

function Get-ActivePowerPlan { Get-PowerPlans | Where-Object Active | Select-Object -First 1 }

function Set-PowerPlan([string]$Id) {
    powercfg /setactive $Id | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not activate power plan $Id." }
}

#endregion

#region Night light
# Windows stores the night light state in an undocumented blob. Only the known format is changed:
# 0x13 at byte 18 = off, 0x15 = on, and "on" has two extra bytes (0x10 0x00) at position 23.

$nightLightPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate'

# $true = on, $false = off, $null = format unknown (not supported on this PC)
function Get-NightLight {
    try { $d = [byte[]](Get-ItemProperty $nightLightPath -ErrorAction Stop).Data } catch { return $null }
    if ($d.Length -eq 43 -and $d[18] -eq 0x15 -and $d[23] -eq 0x10 -and $d[24] -eq 0x00) { return $true }
    if ($d.Length -eq 41 -and $d[18] -eq 0x13) { return $false }
    $null
}

function Set-NightLight([bool]$Enable) {
    $state = Get-NightLight
    if ($null -eq $state) { throw 'Night light format not supported on this PC.' }
    if ($state -eq $Enable) { return }
    $d = [byte[]](Get-ItemProperty $nightLightPath).Data
    if ($Enable) {
        $new = [byte[]]($d[0..22] + [byte[]](0x10, 0x00) + $d[23..($d.Length - 1)])
        $new[18] = 0x15
    } else {
        $new = [byte[]]($d[0..22] + $d[25..($d.Length - 1)])
        $new[18] = 0x13
    }
    # bump the change counter so Windows picks up the new state
    for ($i = 10; $i -lt 15; $i++) { if ($new[$i] -ne 0xff) { $new[$i]++; break } }
    Set-ItemProperty $nightLightPath -Name Data -Value $new -Type Binary
}

#endregion

if ($Action -eq 'status') {
    "Display mode: $([SystemNative]::GetCurrentMode())"
    "Modes: $(([SystemNative]::GetModes() | Select-Object -First 5) -join ' | ') ..."
    "Power plan: $((Get-ActivePowerPlan).Name)"
    "Night light: $(Get-NightLight)"
}
