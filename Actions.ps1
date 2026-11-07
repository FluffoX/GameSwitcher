# Actions on game start and exit (HDR, audio output, closing/starting programs).
# Dot-sourced by Watcher.ps1; GameSwitcher.ps1 uses Get-ActionSettings for the defaults.
# Expects a Write-Log function in the calling script.
. (Join-Path $PSScriptRoot 'HdrControl.ps1')
. (Join-Path $PSScriptRoot 'AudioControl.ps1')

$actionSettingsFile = Join-Path $PSScriptRoot 'settings.json'

# Load settings and fill in defaults
function Get-ActionSettings {
    $a = @{
        Hdr       = @{ Enabled = $true; Revert = $true }
        Audio     = @{ Enabled = $false; DeviceId = ''; DeviceName = ''; Revert = $true }
        CloseApps = @{ Enabled = $false; Apps = @(); Revert = $true }
        StartApps = @{ Enabled = $false; Apps = @(); Revert = $true }
    }
    try {
        $cfg = Get-Content $actionSettingsFile -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
        if ($null -ne $cfg.TurnOffAfterGame) { $a.Hdr.Revert = [bool]$cfg.TurnOffAfterGame }   # old format
        foreach ($name in @($a.Keys)) {
            $src = $cfg.Actions.$name
            if ($src) { foreach ($p in $src.PSObject.Properties) { $a[$name][$p.Name] = $p.Value } }
        }
    } catch {}
    foreach ($name in 'CloseApps', 'StartApps') { $a[$name].Apps = @($a[$name].Apps | Where-Object { $_ }) }
    $a
}

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

# What was changed on game start, so it can be reverted afterwards
$script:changes = $null

function Invoke-GameStart {
    $cfg = Get-ActionSettings
    $script:changes = @{ HdrDisplays = @(); Audio = $null; ClosedApps = @(); StartedApps = @() }

    if ($cfg.Hdr.Enabled) {
        try {
            foreach ($d in Get-HdrDisplays | Where-Object { $_.HdrSupported -and -not $_.HdrEnabled }) {
                Set-Hdr $d $true
                $script:changes.HdrDisplays += $d.Key
                Write-Log "  HDR an auf $($d.Name)"
            }
        } catch { Write-Log "  Fehler HDR: $($_.Exception.Message)" }
    }

    if ($cfg.Audio.Enabled -and $cfg.Audio.DeviceId) {
        try {
            $previous = [AudioNative]::GetDefaultOutputId()
            if ($previous -ne $cfg.Audio.DeviceId) {
                if (-not ([AudioNative]::GetOutputDevices() | Where-Object Id -eq $cfg.Audio.DeviceId)) {
                    throw "Geraet '$($cfg.Audio.DeviceName)' ist nicht verbunden."
                }
                $previousName = [AudioNative]::GetDefaultOutputName()
                [AudioNative]::SetDefaultOutput($cfg.Audio.DeviceId)
                $script:changes.Audio = @{ PreviousId = $previous; PreviousName = $previousName; SetId = $cfg.Audio.DeviceId }
                Write-Log "  Audio -> $($cfg.Audio.DeviceName)"
            }
        } catch { Write-Log "  Fehler Audio: $($_.Exception.Message)" }
    }

    if ($cfg.CloseApps.Enabled) {
        foreach ($app in $cfg.CloseApps.Apps) {
            foreach ($p in @(Get-AppProcesses $app)) {
                $path = $p.Path
                try {
                    Stop-App $p
                    if (-not $path) { $path = $app }
                    if ($script:changes.ClosedApps -notcontains $path) {
                        $script:changes.ClosedApps += $path
                        Write-Log "  Beendet: $([IO.Path]::GetFileName($path))"
                    }
                } catch { Write-Log "  Fehler beim Beenden von $($p.ProcessName): $($_.Exception.Message)" }
            }
        }
    }

    if ($cfg.StartApps.Enabled) {
        foreach ($app in $cfg.StartApps.Apps) {
            if (Get-AppProcesses $app) { continue }   # already running, so don't stop it afterwards either
            try {
                Start-Process -FilePath $app -WorkingDirectory (Split-Path $app)
                $script:changes.StartedApps += $app
                Write-Log "  Gestartet: $([IO.Path]::GetFileName($app))"
            } catch { Write-Log "  Fehler beim Starten von $app : $($_.Exception.Message)" }
        }
    }
}

function Invoke-GameEnd {
    $cfg = Get-ActionSettings
    $c = $script:changes
    if (-not $c) { return }

    if ($c.StartedApps) {
        if ($cfg.StartApps.Revert) {
            foreach ($app in $c.StartedApps) {
                foreach ($p in @(Get-AppProcesses $app)) {
                    try { Stop-App $p } catch { Write-Log "  Fehler beim Beenden von $($p.ProcessName): $($_.Exception.Message)" }
                }
                Write-Log "  Wieder beendet: $([IO.Path]::GetFileName($app))"
            }
        } else { Write-Log '  Gestartete Programme laufen weiter (Einstellung)' }
    }

    if ($c.ClosedApps) {
        if ($cfg.CloseApps.Revert) {
            foreach ($app in $c.ClosedApps) {
                if (Get-AppProcesses $app) { continue }
                try {
                    Start-Process -FilePath $app -WorkingDirectory (Split-Path $app)
                    Write-Log "  Wieder gestartet: $([IO.Path]::GetFileName($app))"
                } catch { Write-Log "  Fehler beim Starten von $app : $($_.Exception.Message)" }
            }
        } else { Write-Log '  Beendete Programme bleiben aus (Einstellung)' }
    }

    if ($c.Audio) {
        if ($cfg.Audio.Revert) {
            try {
                # only revert if nobody switched manually in the meantime
                if ([AudioNative]::GetDefaultOutputId() -eq $c.Audio.SetId -and
                    ([AudioNative]::GetOutputDevices() | Where-Object Id -eq $c.Audio.PreviousId)) {
                    [AudioNative]::SetDefaultOutput($c.Audio.PreviousId)
                    Write-Log "  Audio -> $($c.Audio.PreviousName)"
                }
            } catch { Write-Log "  Fehler Audio: $($_.Exception.Message)" }
        } else { Write-Log '  Audioausgabe bleibt (Einstellung)' }
    }

    if ($c.HdrDisplays) {
        if ($cfg.Hdr.Revert) {
            try {
                foreach ($d in Get-HdrDisplays | Where-Object { $c.HdrDisplays -contains $_.Key -and $_.HdrEnabled }) {
                    Set-Hdr $d $false
                    Write-Log "  HDR aus auf $($d.Name)"
                }
            } catch { Write-Log "  Fehler HDR: $($_.Exception.Message)" }
        } else { Write-Log '  HDR bleibt an (Einstellung)' }
    }

    $script:changes = $null
}
