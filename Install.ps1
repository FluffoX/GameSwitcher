# Sets up GameSwitcher: builds GameSwitcher.exe if needed, registers the background service as a
# logon task (autostart), creates shortcuts in this folder and the Start menu, and starts the service.
#   .\Install.ps1             set up autostart and start the service
#   .\Install.ps1 -NoStart    only set up autostart
#   .\Install.ps1 -Uninstall  remove autostart and shortcuts and stop the service
param([switch]$Uninstall, [switch]$NoStart)

$taskName = 'GameSwitcher'
$exe = Join-Path $PSScriptRoot 'GameSwitcher.exe'
$startMenuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'GameSwitcher.lnk'

function Stop-Watcher {
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='GameSwitcher.exe'" |
        Where-Object { $_.CommandLine -like '*Watcher.ps1*' -or $_.CommandLine -like '*--watcher*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
}

if ($Uninstall) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Stop-Watcher
    Remove-Item $startMenuLink, (Join-Path $PSScriptRoot 'GameSwitcher.lnk') -ErrorAction SilentlyContinue
    Write-Host "'$taskName' removed."
    return
}

if (-not (Test-Path $exe)) {
    try { & (Join-Path $PSScriptRoot 'Build.ps1') | Out-Null } catch { Write-Warning $_.Exception.Message }
}

# GameSwitcher.exe runs without any window; without it, conhost --headless hides PowerShell
# (even when Windows Terminal is the default terminal)
if (Test-Path $exe) {
    $action = New-ScheduledTaskAction -Execute $exe -Argument '--watcher' -WorkingDirectory $PSScriptRoot
} else {
    $watcher = Join-Path $PSScriptRoot 'Watcher.ps1'
    $action = New-ScheduledTaskAction -Execute 'conhost.exe' `
        -Argument "--headless powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File `"$watcher`""
}
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings `
    -Principal $principal -Description 'Applies the GameSwitcher actions while a selected game is running.' -Force | Out-Null
if (-not $NoStart) { Start-ScheduledTask -TaskName $taskName }

# Shortcuts for the GUI
$shell = New-Object -ComObject WScript.Shell
foreach ($path in (Join-Path $PSScriptRoot 'GameSwitcher.lnk'), $startMenuLink) {
    $lnk = $shell.CreateShortcut($path)
    if (Test-Path $exe) {
        $lnk.TargetPath = $exe
        $lnk.Arguments = ''
        $lnk.IconLocation = "$exe,0"
    } else {
        $lnk.TargetPath = "$env:WINDIR\System32\conhost.exe"
        $lnk.Arguments = "--headless powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File `"$(Join-Path $PSScriptRoot 'GameSwitcher.ps1')`""
        $lnk.IconLocation = Join-Path $PSScriptRoot 'assets\icon.ico'
    }
    $lnk.WorkingDirectory = $PSScriptRoot
    $lnk.Description = 'GameSwitcher'
    $lnk.Save()
}
Write-Host "'$taskName' installed."
