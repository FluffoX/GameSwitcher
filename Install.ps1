# Registers the watcher as a logon task (autostart) and starts it.
#   .\Install.ps1             set up autostart and start the service
#   .\Install.ps1 -NoStart    only set up autostart
#   .\Install.ps1 -Uninstall  remove autostart and stop the service
param([switch]$Uninstall, [switch]$NoStart)

$taskName = 'GameSwitcher'

if ($Uninstall) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -like '*Watcher.ps1*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    Write-Host "'$taskName' removed."
    return
}

# conhost --headless runs PowerShell without any window (even when Windows Terminal is the default)
$watcher = Join-Path $PSScriptRoot 'Watcher.ps1'
$action = New-ScheduledTaskAction -Execute 'conhost.exe' `
    -Argument "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$watcher`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings `
    -Principal $principal -Description 'Applies the GameSwitcher actions while a selected game is running.' -Force | Out-Null
if (-not $NoStart) { Start-ScheduledTask -TaskName $taskName }

# Shortcut for the GUI (also starts without a console window)
$lnkPath = Join-Path $PSScriptRoot 'GameSwitcher.lnk'
if (-not (Test-Path $lnkPath)) {
    $lnk = (New-Object -ComObject WScript.Shell).CreateShortcut($lnkPath)
    $lnk.TargetPath = "$env:WINDIR\System32\conhost.exe"
    $lnk.Arguments = "--headless powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File `"$(Join-Path $PSScriptRoot 'GameSwitcher.ps1')`""
    $lnk.WorkingDirectory = $PSScriptRoot
    $lnk.IconLocation = "$env:WINDIR\System32\shell32.dll,15"
    $lnk.Description = 'GameSwitcher'
    $lnk.Save()
}
Write-Host "'$taskName' installed."
