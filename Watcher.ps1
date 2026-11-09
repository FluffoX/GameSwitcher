# Background service with a tray icon. Detects running games (the selected ones, optionally every game
# Windows knows, or the manual game mode via hotkey), applies the actions of the matching profile,
# reverts them when the game exits and records play time.
$root = $PSScriptRoot
$logFile = Join-Path $root 'watcher.log'
$pausePath = Join-Path $root 'pause.json'
$playtimePath = Join-Path $root 'playtime.json'
$idleSecondsBeforeRevert = 10   # covers launchers that restart the game process

function Write-Log([string]$Message) {
    "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $Message" | Add-Content -Path $logFile -Encoding UTF8
}

. (Join-Path $root 'Actions.ps1')

# Allow only one instance
$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\GameSwitcherWatcher', [ref]$createdNew)
if (-not $createdNew) { return }

# Keep the log small
if ((Test-Path $logFile) -and (Get-Item $logFile).Length -gt 256KB) {
    $tail = Get-Content $logFile -Tail 500 -Encoding UTF8
    Set-Content $logFile $tail -Encoding UTF8
}

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
if (-not ('HotkeyWindow' -as [type])) {
    Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Invisible window that receives a global hotkey
public class HotkeyWindow : NativeWindow {
    public event EventHandler Pressed;
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    const uint MOD_NOREPEAT = 0x4000;
    bool registered;

    public HotkeyWindow() { CreateHandle(new CreateParams()); }

    public bool Register(uint modifiers, uint vk) {
        Unregister();
        registered = RegisterHotKey(Handle, 1, modifiers | MOD_NOREPEAT, vk);
        return registered;
    }

    public void Unregister() {
        if (registered) { UnregisterHotKey(Handle, 1); registered = false; }
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x0312 && Pressed != null) Pressed(this, EventArgs.Empty);
        base.WndProc(ref m);
    }
}
'@
}

#region State

$script:cfg = $null
$script:cfgTime = $null
$script:exeMap = @{}        # process name (lower case, no .exe) -> @{ Key; Name }
$script:libraryMap = @{}    # same for every game in the GUI's list
$script:autoMap = @{}       # same for games Windows knows (GameConfigStore)
$script:autoMapTime = [datetime]::MinValue
$script:hotkeyText = $null
$script:manual = $false
$script:active = $null      # @{ Key; Name } of the game whose profile is applied
$script:idleSeconds = 0
$script:play = @{}          # game key -> @{ Name; Last = [datetime] } for play time
$script:playFlush = Get-Date
$bootTime = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToString('o')

#endregion

#region Configuration

# "Ctrl+Alt+G" -> modifier flags and virtual key
function ConvertTo-Hotkey([string]$Text) {
    $mods = 0; $vk = 0
    foreach ($part in $Text -split '\+') {
        switch ($part.Trim()) {
            'Ctrl' { $mods = $mods -bor 2 }
            'Alt' { $mods = $mods -bor 1 }
            'Shift' { $mods = $mods -bor 4 }
            'Win' { $mods = $mods -bor 8 }
            default { try { $vk = [int][System.Windows.Forms.Keys]$part.Trim() } catch {} }
        }
    }
    if ($vk) { @{ Mods = [uint32]$mods; Vk = [uint32]$vk } }
}

function Update-Config {
    $item = Get-Item $settingsPath -ErrorAction SilentlyContinue
    $time = if ($item) { $item.LastWriteTimeUtc } else { $null }
    if ($null -ne $script:cfgTime -and $time -eq $script:cfgTime) { return }
    $script:cfgTime = $time
    $script:cfg = Read-Settings
    Initialize-Language (Get-ConfiguredLanguage)

    $map = @{}
    foreach ($g in $script:cfg.Selected) {
        foreach ($exe in $g.Exes) { $map[($exe -replace '\.exe$', '').ToLower()] = @{ Key = $g.Key; Name = $g.Name } }
    }
    $script:exeMap = $map
    $lib = @{}
    foreach ($g in $script:cfg.Library) {
        foreach ($exe in $g.Exes) { $lib[($exe -replace '\.exe$', '').ToLower()] = @{ Key = $g.Key; Name = $g.Name } }
    }
    $script:libraryMap = $lib
    $script:autoMapTime = [datetime]::MinValue

    $hk = [string]$script:cfg.Hotkey
    if ($hk -ne $script:hotkeyText) {
        $script:hotkeyText = $hk
        $hotkey.Unregister()
        $parsed = if ($hk) { ConvertTo-Hotkey $hk }
        if ($parsed -and -not $hotkey.Register($parsed.Mods, $parsed.Vk)) { Write-Log (T 'log.hotkeyFailed' $hk) }
    }
    Update-TrayTexts
}

# Games Windows has seen running (Game Bar / Game Mode know them as games)
function Update-AutoMap {
    if (-not $script:cfg.AutoDetect) { $script:autoMap = @{}; return }
    if (((Get-Date) - $script:autoMapTime).TotalSeconds -lt 60) { return }
    $script:autoMapTime = Get-Date
    $map = @{}
    foreach ($k in Get-ChildItem 'HKCU:\System\GameConfigStore\Children' -ErrorAction SilentlyContinue) {
        $path = (Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue).MatchedExeFullPath
        if (-not $path) { continue }
        $exe = [IO.Path]::GetFileNameWithoutExtension($path).ToLower()
        # a game from the GUI's list keeps its key (profile, play time); others get the same key the GUI uses
        if ($script:libraryMap.ContainsKey($exe)) { $map[$exe] = $script:libraryMap[$exe]; continue }
        $name = $null
        try { $name = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductName } catch {}
        if (-not $name -or -not $name.Trim()) { $name = $exe }
        $map[$exe] = @{ Key = 'auto:' + $path.ToLower(); Name = $name.Trim() }
    }
    $script:autoMap = $map
}

function Get-PauseInfo {
    if (-not (Test-Path $pausePath)) { return $null }
    try { $p = Get-Content $pausePath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $null }
    if ($p.UntilReboot -and $p.BootTime -eq $bootTime) { return $p }
    if ($p.Until -and [datetime]$p.Until -gt (Get-Date)) { return $p }
    Remove-Item $pausePath -ErrorAction SilentlyContinue   # expired
    $null
}

function Set-Pause([int]$Hours, [switch]$UntilReboot, [switch]$Resume) {
    if ($Resume) { Remove-Item $pausePath -ErrorAction SilentlyContinue; Write-Log (T 'log.resumed') }
    else {
        $p = if ($UntilReboot) { @{ UntilReboot = $true; BootTime = $bootTime } } else { @{ Until = (Get-Date).AddHours($Hours).ToString('o') } }
        $p | ConvertTo-Json | Set-Content $pausePath -Encoding UTF8
        Write-Log (T 'log.paused')
    }
    Invoke-Tick
}

#endregion

#region Play time

function Add-Playtime([string]$Key, [string]$Name, [double]$Seconds) {
    $data = @{}
    if (Test-Path $playtimePath) {
        try { foreach ($m in Get-Members (Get-Content $playtimePath -Raw -Encoding UTF8 | ConvertFrom-Json)) { $data[$m.Name] = $m.Value } } catch {}
    }
    $old = $data[$Key]
    $data[$Key] = @{ Name = $Name; Seconds = [math]::Round([double]$old.Seconds + $Seconds); LastPlayed = (Get-Date).ToString('o') }
    $data | ConvertTo-Json -Depth 4 | Set-Content $playtimePath -Encoding UTF8
}

function Update-Playtime($Running) {
    $now = Get-Date
    $keys = @($Running | ForEach-Object { $_.Key })
    foreach ($g in $Running) {
        if (-not $script:play.ContainsKey($g.Key)) { $script:play[$g.Key] = @{ Name = $g.Name; Last = $now } }
    }
    $flush = ($now - $script:playFlush).TotalSeconds -ge 60
    foreach ($key in @($script:play.Keys)) {
        $p = $script:play[$key]
        if ($keys -notcontains $key) {
            Add-Playtime $key $p.Name ($now - $p.Last).TotalSeconds
            $script:play.Remove($key)
        } elseif ($flush) {
            Add-Playtime $key $p.Name ($now - $p.Last).TotalSeconds
            $p.Last = $now
        }
    }
    if ($flush) { $script:playFlush = $now }
}

#endregion

#region Detection

function Get-RunningGames {
    $names = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($p in [System.Diagnostics.Process]::GetProcesses()) { [void]$names.Add($p.ProcessName.ToLower()) }
    $found = [ordered]@{}
    foreach ($map in $script:exeMap, $script:autoMap) {
        foreach ($exe in $map.Keys) {
            if ($names.Contains($exe)) {
                $g = $map[$exe]
                if (-not $found.Contains($g.Key)) { $found[$g.Key] = $g }
            }
        }
    }
    @($found.Values)
}

function Show-Notification([string]$Text) {
    if ($script:cfg -and $script:cfg.Notifications -eq $false) { return }
    $tray.ShowBalloonTip(4000, 'GameSwitcher', $Text, [System.Windows.Forms.ToolTipIcon]::Info)
}

function Start-Session($Game) {
    $script:active = $Game
    $script:idleSeconds = 0
    Write-Log $(if ($Game.Key -eq 'manual') { T 'log.manualStart' } else { T 'log.gameStarted' $Game.Name })
    Invoke-GameStart $Game.Key $Game.Name
    Show-Notification (T 'notify.start' $Game.Name)
}

function Stop-Session {
    Write-Log $(if ($script:active.Key -eq 'manual') { T 'log.manualStop' } else { T 'log.noGame' })
    Invoke-GameEnd
    $script:active = $null
    Show-Notification (T 'notify.end')
}

function Invoke-Tick {
    try {
        Update-Config
        Update-AutoMap
        $running = @(Get-RunningGames)
        Update-Playtime $running
        if ($script:manual) { $running = @(@{ Key = 'manual'; Name = (T 'tray.manual') }) + $running }
        $paused = Get-PauseInfo
        if ($paused) { $running = @() }

        if ($running.Count -and -not $script:active) {
            Start-Session $running[0]
        } elseif ($running.Count) {
            $script:idleSeconds = 0
        } elseif ($script:active) {
            $script:idleSeconds++
            # manual mode and pause end at once; a game gets a grace period for launcher restarts
            if ($script:active.Key -eq 'manual' -or $paused -or $script:idleSeconds -ge $idleSecondsBeforeRevert) { Stop-Session }
        }
        Update-TrayTexts $paused
    } catch {
        Write-Log (T 'log.error' $_.Exception.Message)
    }
}

#endregion

#region Tray icon

$hotkey = New-Object HotkeyWindow
$hotkey.add_Pressed({ $script:manual = -not $script:manual; Invoke-Tick })

$tray = New-Object System.Windows.Forms.NotifyIcon
$iconPath = Join-Path $root 'assets\icon.ico'
$tray.Icon = if (Test-Path $iconPath) { New-Object System.Drawing.Icon($iconPath, 16, 16) } else { [System.Drawing.SystemIcons]::Application }
$trayMenu = New-Object System.Windows.Forms.ContextMenuStrip
$miStatus = $trayMenu.Items.Add(''); $miStatus.Enabled = $false
[void]$trayMenu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
$miOpen = $trayMenu.Items.Add('')
$miOpen.Font = New-Object System.Drawing.Font($miOpen.Font, [System.Drawing.FontStyle]::Bold)
$miHdr = $trayMenu.Items.Add('')
$miManual = $trayMenu.Items.Add('')
$miPause = $trayMenu.Items.Add('')
$miPause1 = $miPause.DropDownItems.Add('')
$miPause2 = $miPause.DropDownItems.Add('')
$miPauseReboot = $miPause.DropDownItems.Add('')
[void]$miPause.DropDownItems.Add((New-Object System.Windows.Forms.ToolStripSeparator))
$miResume = $miPause.DropDownItems.Add('')
[void]$trayMenu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
$miExit = $trayMenu.Items.Add('')
$tray.ContextMenuStrip = $trayMenu

function Update-TrayTexts($Paused = (Get-PauseInfo)) {
    $miOpen.Text = T 'tray.open'
    $miHdr.Text = T 'btn.hdrToggle'
    $miManual.Text = if ($script:manual) { T 'tray.manualStop' } else { T 'tray.manualStart' }
    if ($script:hotkeyText) { $miManual.Text += "   ($script:hotkeyText)" }
    $miPause.Text = T 'btn.pause'
    $miPause1.Text = T 'pause.1h'
    $miPause2.Text = T 'pause.2h'
    $miPauseReboot.Text = T 'pause.reboot'
    $miResume.Text = T 'btn.resume'
    $miResume.Enabled = [bool]$Paused
    $miExit.Text = T 'tray.exit'
    $miStatus.Text = if ($Paused) {
        if ($Paused.UntilReboot) { T 'pause.untilReboot' } else { T 'pause.until' ([datetime]$Paused.Until).ToString('t') }
    } elseif ($script:active) { T 'tray.active' $script:active.Name } else { T 'tray.idle' }
    $tip = "GameSwitcher – $($miStatus.Text)"
    $tray.Text = if ($tip.Length -gt 63) { $tip.Substring(0, 60) + '...' } else { $tip }
}

function Open-Gui {
    $exe = Join-Path $root 'GameSwitcher.exe'
    if (Test-Path $exe) { Start-Process $exe }
    else { Start-Process conhost.exe -ArgumentList "--headless powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File `"$(Join-Path $root 'GameSwitcher.ps1')`"" }
}

$tray.add_DoubleClick({ Open-Gui })
$miOpen.add_Click({ Open-Gui })
$miHdr.add_Click({
    try {
        $displays = @(Get-HdrDisplays | Where-Object HdrSupported)
        $enable = -not ($displays | Where-Object HdrEnabled)
        foreach ($d in $displays) { if ($d.HdrEnabled -ne $enable) { Set-Hdr $d $enable } }
    } catch { Write-Log (T 'log.hdrError' $_.Exception.Message) }
})
$miManual.add_Click({ $script:manual = -not $script:manual; Invoke-Tick })
$miPause1.add_Click({ Set-Pause -Hours 1 })
$miPause2.add_Click({ Set-Pause -Hours 2 })
$miPauseReboot.add_Click({ Set-Pause -UntilReboot })
$miResume.add_Click({ Set-Pause -Resume })
$miExit.add_Click({
    $timer.Stop()
    if ($script:active) { Stop-Session }
    $tray.Visible = $false
    $hotkey.Unregister()
    [System.Windows.Forms.Application]::ExitThread()
})

#endregion

Update-Config
Write-Log (T 'log.watcherStarted')

# A state file left by a crash: adopt it if that game is still running, otherwise revert it now
$pending = Get-PendingState
if ($pending) {
    Update-AutoMap
    $stillRunning = @(Get-RunningGames) | Where-Object { $_.Key -eq $pending.GameKey } | Select-Object -First 1
    $script:changes = $pending
    if ($stillRunning) {
        $script:active = $stillRunning
        if ($pending.KeepAwake) { [SystemNative]::KeepAwake($true) }
    } else {
        Write-Log (T 'log.recovered')
        Invoke-GameEnd
    }
}

$tray.Visible = $true
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 1000
$timer.add_Tick({ Invoke-Tick })
$timer.Start()
Invoke-Tick
[System.Windows.Forms.Application]::Run()
$tray.Dispose()
