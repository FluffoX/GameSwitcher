# Actions on game start and exit, per-game profiles and crash-safe reverting.
# Dot-sourced by Watcher.ps1 and GameSwitcher.ps1. Expects a Write-Log function in the calling script
# (only needed for Invoke-GameStart / Invoke-GameEnd).
. (Join-Path $PSScriptRoot 'AppInfo.ps1')
. (Join-Path $PSScriptRoot 'Strings.ps1')
. (Join-Path $PSScriptRoot 'HdrControl.ps1')
. (Join-Path $PSScriptRoot 'AudioControl.ps1')
. (Join-Path $PSScriptRoot 'SystemControl.ps1')

$settingsPath = Join-Path $PSScriptRoot 'settings.json'
$statePath = Join-Path $PSScriptRoot 'state.json'

#region Settings and profiles

function New-DefaultActions {
    @{
        Hdr        = @{ Enabled = $true; Revert = $true }
        Sdr        = @{ Enabled = $false; Level = 30; Revert = $true }
        Display    = @{ Enabled = $false; Width = 0; Height = 0; Hz = 0; Revert = $true }
        Monitors   = @{ Enabled = $false; Revert = $true }
        NightLight = @{ Enabled = $false; Revert = $true }
        Audio      = @{ Enabled = $false; DeviceId = ''; DeviceName = ''; Revert = $true }
        Mic        = @{ Enabled = $false; DeviceId = ''; DeviceName = ''; Revert = $true }
        Volume     = @{ Enabled = $false; Level = 50; Revert = $true }
        Power      = @{ Enabled = $false; PlanId = ''; PlanName = ''; Revert = $true }
        KeepAwake  = @{ Enabled = $false }
        CloseApps  = @{ Enabled = $false; Apps = @(); Revert = $true }
        StartApps  = @{ Enabled = $false; Apps = @(); Revert = $true }
        Script     = @{ Enabled = $false; Start = ''; End = '' }
    }
}

# Name/value pairs of a hashtable or a JSON object
function Get-Members($Object) {
    if ($null -eq $Object) { return }
    if ($Object -is [System.Collections.IDictionary]) {
        foreach ($k in @($Object.Keys)) { [pscustomobject]@{ Name = $k; Value = $Object[$k] } }
    } else {
        foreach ($p in $Object.PSObject.Properties) { [pscustomobject]@{ Name = $p.Name; Value = $p.Value } }
    }
}

# A full action set: defaults overlaid with the stored values (also completes profiles from older versions)
function Complete-Actions($Source) {
    $a = New-DefaultActions
    foreach ($action in Get-Members $Source) {
        if (-not $a.ContainsKey($action.Name)) { continue }
        foreach ($field in Get-Members $action.Value) { $a[$action.Name][$field.Name] = $field.Value }
    }
    foreach ($name in 'CloseApps', 'StartApps') { $a[$name].Apps = @($a[$name].Apps | Where-Object { $_ }) }
    $a
}

function Read-Settings {
    try { Get-Content $settingsPath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json } catch { $null }
}

# Actions for a game: its own profile if it has one, otherwise the default profile
function Get-ActionSettings([string]$GameKey) {
    $cfg = Read-Settings
    if ($GameKey -and $cfg.Profiles) {
        $own = Get-Members $cfg.Profiles | Where-Object Name -eq $GameKey | Select-Object -First 1
        if ($own) { return Complete-Actions $own.Value }
    }
    $a = Complete-Actions $cfg.Actions
    if ($null -ne $cfg.TurnOffAfterGame -and -not $cfg.Actions) { $a.Hdr.Revert = [bool]$cfg.TurnOffAfterGame }   # version 1.0 format
    $a
}

#endregion

#region Helpers

# Closes a program gracefully (closes its window), forcibly if needed
function Stop-App([System.Diagnostics.Process]$Process) {
    try {
        if ($Process.MainWindowHandle -ne 0 -and $Process.CloseMainWindow() -and $Process.WaitForExit(5000)) { return }
        if (-not $Process.HasExited) { $Process.Kill() }
    } catch {
        if (-not $Process.HasExited) { throw }
    }
}

function Get-AppProcesses([string]$Path) {
    Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Path)) -ErrorAction SilentlyContinue
}

# Runs a user command hidden; scripts and programs can be given as a plain path.
# The game name is passed in the environment variable GAMESWITCHER_GAME.
function Invoke-UserCommand([string]$Command, [string]$GameName) {
    $Command = $Command.Trim()
    if (-not $Command) { return }
    $env:GAMESWITCHER_GAME = $GameName
    $file = $Command.Trim('"')
    if ((Test-Path $file -PathType Leaf) -and $file -like '*.ps1') {
        Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$file`"" -WindowStyle Hidden
    } elseif (Test-Path $file -PathType Leaf) {
        Start-Process -FilePath $file -WorkingDirectory (Split-Path $file)
    } else {
        Start-Process -FilePath $env:ComSpec -ArgumentList "/c $Command" -WindowStyle Hidden
    }
}

# Runs one action; an error is logged but never stops the other actions. The state is saved after every step.
function Invoke-Step([string]$Name, [scriptblock]$Body) {
    try { & $Body } catch { Write-Log (T 'log.actionError' $Name $_.Exception.Message) }
    Save-State
}

#endregion

#region Crash-safe state
# Everything changed on game start is written to state.json right away. If the PC or the service
# crashes during a game, the next service start finds the file and reverts the changes.

$script:changes = $null

function Save-State {
    if ($script:changes) { $script:changes | ConvertTo-Json -Depth 6 | Set-Content $statePath -Encoding UTF8 }
}

function Get-PendingState {
    if (-not (Test-Path $statePath)) { return $null }
    try { Get-Content $statePath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $null }
}

function Clear-State { Remove-Item $statePath -ErrorAction SilentlyContinue }

#endregion

function Invoke-GameStart([string]$GameKey, [string]$GameName) {
    Initialize-Language (Get-ConfiguredLanguage)
    $cfg = Get-ActionSettings $GameKey
    $c = @{
        GameKey = $GameKey; GameName = $GameName
        Monitors = $false; Display = $null; HdrDisplays = @(); Sdr = @(); NightLight = $false
        Audio = $null; Mic = $null; Volume = $null; Power = $null; KeepAwake = $false
        ClosedApps = @(); StartedApps = @(); ScriptEnd = ''
    }
    $script:changes = $c
    Save-State

    if ($cfg.Monitors.Enabled) {
        Invoke-Step (T 'act.monitors') {
            if ([HdrNative]::CountActiveDisplays() -gt 1) {
                $r = [HdrNative]::DetachSecondary()
                if ($r -ne 0) { throw "SetDisplayConfig error $r" }
                $c.Monitors = $true
                Write-Log (T 'log.monitorsOff')
                Start-Sleep -Milliseconds 1500
            }
        }
    }

    if ($cfg.Display.Enabled -and $cfg.Display.Width) {
        Invoke-Step (T 'act.display') {
            $cur = [SystemNative]::GetCurrentMode()
            if ($cur.Width -ne $cfg.Display.Width -or $cur.Height -ne $cfg.Display.Height -or $cur.Hz -ne $cfg.Display.Hz) {
                $r = [SystemNative]::SetMode($cfg.Display.Width, $cfg.Display.Height, $cfg.Display.Hz)
                if ($r -ne 0) { throw "ChangeDisplaySettingsEx error $r" }
                $set = [SystemNative]::GetCurrentMode()
                $c.Display = @{ Width = $cur.Width; Height = $cur.Height; Hz = $cur.Hz; SetWidth = $set.Width; SetHeight = $set.Height; SetHz = $set.Hz }
                Write-Log (T 'log.displaySet' ([SystemNative]::GetCurrentMode()))
            }
        }
    }

    if ($cfg.Hdr.Enabled) {
        Invoke-Step 'HDR' {
            foreach ($d in Get-HdrDisplays | Where-Object { $_.HdrSupported -and -not $_.HdrEnabled }) {
                Set-Hdr $d $true
                $c.HdrDisplays += $d.Key
                Write-Log (T 'log.hdrOn' $d.Name)
            }
            if ($c.HdrDisplays) { Start-Sleep -Milliseconds 1000 }   # let Windows finish the switch
        }
    }

    if ($cfg.Sdr.Enabled) {
        Invoke-Step (T 'act.sdr') {
            foreach ($d in Get-HdrDisplays | Where-Object HdrEnabled) {
                $prev = Get-SdrBrightness $d
                if ($prev -ne [int]$cfg.Sdr.Level) {
                    Set-SdrBrightness $d $cfg.Sdr.Level
                    $c.Sdr += @{ Key = $d.Key; Level = $prev }
                    Write-Log (T 'log.sdrSet' $cfg.Sdr.Level $d.Name)
                }
            }
        }
    }

    if ($cfg.NightLight.Enabled) {
        Invoke-Step (T 'act.nightLight') {
            $state = Get-NightLight
            if ($null -eq $state) { Write-Log (T 'log.unsupported' (T 'act.nightLight')) }
            elseif ($state) { Set-NightLight $false; $c.NightLight = $true; Write-Log (T 'log.nightLightOff') }
        }
    }

    foreach ($kind in 'Audio', 'Mic') {
        $a = $cfg[$kind]
        if (-not ($a.Enabled -and $a.DeviceId)) { continue }
        $flow = if ($kind -eq 'Mic') { [AudioNative]::Input } else { [AudioNative]::Output }
        Invoke-Step $kind {
            $previous = [AudioNative]::GetDefaultId($flow)
            if ($previous -ne $a.DeviceId) {
                if (-not ([AudioNative]::GetDevices($flow) | Where-Object Id -eq $a.DeviceId)) { throw (T 'log.audioMissing' $a.DeviceName) }
                $previousName = [AudioNative]::GetDefaultName($flow)
                [AudioNative]::SetDefault($a.DeviceId, $kind -eq 'Mic')
                $c[$kind] = @{ PreviousId = $previous; PreviousName = $previousName; SetId = $a.DeviceId }
                Write-Log (T $(if ($kind -eq 'Mic') { 'log.micSwitched' } else { 'log.audioSwitched' }) $a.DeviceName)
            }
        }
    }

    if ($cfg.Volume.Enabled) {
        Invoke-Step (T 'act.volume') {
            $prev = [AudioNative]::GetVolume()
            if ($prev -ge 0 -and $prev -ne [int]$cfg.Volume.Level) {
                [AudioNative]::SetVolume([int]$cfg.Volume.Level)
                $c.Volume = @{ Previous = $prev; Set = [int]$cfg.Volume.Level }
                Write-Log (T 'log.volumeSet' $cfg.Volume.Level)
            }
        }
    }

    if ($cfg.Power.Enabled -and $cfg.Power.PlanId) {
        Invoke-Step (T 'act.power') {
            $active = Get-ActivePowerPlan
            if ($active.Id -ne $cfg.Power.PlanId) {
                Set-PowerPlan $cfg.Power.PlanId
                $c.Power = @{ PreviousId = $active.Id; PreviousName = $active.Name }
                Write-Log (T 'log.powerSet' $cfg.Power.PlanName)
            }
        }
    }

    if ($cfg.KeepAwake.Enabled) {
        Invoke-Step (T 'act.keepAwake') {
            [SystemNative]::KeepAwake($true)
            $c.KeepAwake = $true
            Write-Log (T 'log.keepAwake')
        }
    }

    if ($cfg.CloseApps.Enabled) {
        foreach ($app in $cfg.CloseApps.Apps) {
            foreach ($p in @(Get-AppProcesses $app)) {
                $path = $p.Path
                try {
                    Stop-App $p
                    if (-not $path) { $path = $app }
                    if ($c.ClosedApps -notcontains $path) {
                        $c.ClosedApps += $path
                        Write-Log (T 'log.closed' ([IO.Path]::GetFileName($path)))
                    }
                } catch { Write-Log (T 'log.closeError' $p.ProcessName $_.Exception.Message) }
            }
        }
        Save-State
    }

    if ($cfg.StartApps.Enabled) {
        foreach ($app in $cfg.StartApps.Apps) {
            if (Get-AppProcesses $app) { continue }   # already running, so don't stop it afterwards either
            try {
                Start-Process -FilePath $app -WorkingDirectory (Split-Path $app)
                $c.StartedApps += $app
                Write-Log (T 'log.started' ([IO.Path]::GetFileName($app)))
            } catch { Write-Log (T 'log.startError' $app $_.Exception.Message) }
        }
        Save-State
    }

    if ($cfg.Script.Enabled) {
        Invoke-Step (T 'act.script') {
            if ($cfg.Script.Start) { Invoke-UserCommand $cfg.Script.Start $GameName; Write-Log (T 'log.scriptRun' $cfg.Script.Start) }
            $c.ScriptEnd = [string]$cfg.Script.End
        }
    }
}

# Reverts what Invoke-GameStart changed (or what a crashed session left behind, see Get-PendingState)
function Invoke-GameEnd {
    Initialize-Language (Get-ConfiguredLanguage)
    $c = $script:changes
    if (-not $c) { Clear-State; return }
    $cfg = Get-ActionSettings $c.GameKey

    if ($c.ScriptEnd) {
        try { Invoke-UserCommand $c.ScriptEnd $c.GameName; Write-Log (T 'log.scriptRun' $c.ScriptEnd) }
        catch { Write-Log (T 'log.actionError' (T 'act.script') $_.Exception.Message) }
    }

    if ($c.StartedApps) {
        if ($cfg.StartApps.Revert) {
            foreach ($app in $c.StartedApps) {
                foreach ($p in @(Get-AppProcesses $app)) {
                    try { Stop-App $p } catch { Write-Log (T 'log.closeError' $p.ProcessName $_.Exception.Message) }
                }
                Write-Log (T 'log.closedAgain' ([IO.Path]::GetFileName($app)))
            }
        } else { Write-Log (T 'log.startedKept') }
    }

    if ($c.ClosedApps) {
        if ($cfg.CloseApps.Revert) {
            foreach ($app in $c.ClosedApps) {
                if (Get-AppProcesses $app) { continue }
                try {
                    Start-Process -FilePath $app -WorkingDirectory (Split-Path $app)
                    Write-Log (T 'log.restarted' ([IO.Path]::GetFileName($app)))
                } catch { Write-Log (T 'log.startError' $app $_.Exception.Message) }
            }
        } else { Write-Log (T 'log.closedKept') }
    }

    if ($c.KeepAwake) { [SystemNative]::KeepAwake($false) }

    if ($c.Power -and $cfg.Power.Revert) {
        try {
            if ((Get-ActivePowerPlan).Id -eq $cfg.Power.PlanId) {
                Set-PowerPlan $c.Power.PreviousId
                Write-Log (T 'log.powerSet' $c.Power.PreviousName)
            }
        } catch { Write-Log (T 'log.actionError' (T 'act.power') $_.Exception.Message) }
    }

    if ($c.Volume -and $cfg.Volume.Revert) {
        try {
            # only revert if the volume was not changed by hand in the meantime
            if ([AudioNative]::GetVolume() -eq [int]$c.Volume.Set) {
                [AudioNative]::SetVolume([int]$c.Volume.Previous)
                Write-Log (T 'log.volumeSet' $c.Volume.Previous)
            }
        } catch { Write-Log (T 'log.actionError' (T 'act.volume') $_.Exception.Message) }
    }

    foreach ($kind in 'Mic', 'Audio') {
        $saved = $c.$kind
        if (-not $saved) { continue }
        if ($cfg[$kind].Revert) {
            $flow = if ($kind -eq 'Mic') { [AudioNative]::Input } else { [AudioNative]::Output }
            try {
                # only revert if nobody switched manually in the meantime
                if ([AudioNative]::GetDefaultId($flow) -eq $saved.SetId -and
                    ([AudioNative]::GetDevices($flow) | Where-Object Id -eq $saved.PreviousId)) {
                    [AudioNative]::SetDefault($saved.PreviousId, $kind -eq 'Mic')
                    Write-Log (T $(if ($kind -eq 'Mic') { 'log.micSwitched' } else { 'log.audioSwitched' }) $saved.PreviousName)
                }
            } catch { Write-Log (T 'log.audioError' $_.Exception.Message) }
        } else { Write-Log (T 'log.audioKept') }
    }

    if ($c.NightLight -and $cfg.NightLight.Revert) {
        try { Set-NightLight $true; Write-Log (T 'log.nightLightOn') }
        catch { Write-Log (T 'log.actionError' (T 'act.nightLight') $_.Exception.Message) }
    }

    if ($c.Sdr -and $cfg.Sdr.Revert) {
        try {
            foreach ($d in Get-HdrDisplays | Where-Object HdrEnabled) {
                $saved = $c.Sdr | Where-Object { $_.Key -eq $d.Key } | Select-Object -First 1
                if ($saved -and $saved.Level -ge 0) { Set-SdrBrightness $d $saved.Level; Write-Log (T 'log.sdrSet' $saved.Level $d.Name) }
            }
        } catch { Write-Log (T 'log.actionError' (T 'act.sdr') $_.Exception.Message) }
    }

    if ($c.HdrDisplays) {
        if ($cfg.Hdr.Revert) {
            try {
                foreach ($d in Get-HdrDisplays | Where-Object { $c.HdrDisplays -contains $_.Key -and $_.HdrEnabled }) {
                    Set-Hdr $d $false
                    Write-Log (T 'log.hdrOff' $d.Name)
                }
            } catch { Write-Log (T 'log.hdrError' $_.Exception.Message) }
        } else { Write-Log (T 'log.hdrKept') }
    }

    if ($c.Display -and $cfg.Display.Revert) {
        try {
            $cur = [SystemNative]::GetCurrentMode()
            if ($cur.Width -eq $c.Display.SetWidth -and $cur.Height -eq $c.Display.SetHeight -and $cur.Hz -eq $c.Display.SetHz) {
                $r = [SystemNative]::SetMode($c.Display.Width, $c.Display.Height, $c.Display.Hz)
                if ($r -ne 0) { throw "ChangeDisplaySettingsEx error $r" }
                Write-Log (T 'log.displaySet' ([SystemNative]::GetCurrentMode()))
            }
        } catch { Write-Log (T 'log.actionError' (T 'act.display') $_.Exception.Message) }
    }

    if ($c.Monitors -and $cfg.Monitors.Revert) {
        try {
            $r = [HdrNative]::RestoreExtended()
            if ($r -ne 0) { throw "SetDisplayConfig error $r" }
            Write-Log (T 'log.monitorsOn')
        } catch { Write-Log (T 'log.actionError' (T 'act.monitors') $_.Exception.Message) }
    }

    $script:changes = $null
    Clear-State
}
