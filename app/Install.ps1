# Sets up autostart (a logon task that runs the background service) and the Start menu entry.
# Called by the app (first start, autostart checkbox) and by the uninstaller; for source checkouts it
# also builds GameSwitcher.exe first.
#   app\Install.ps1             set up autostart and start the service
#   app\Install.ps1 -NoStart    only set up autostart
#   app\Install.ps1 -Uninstall  remove autostart and the Start menu entry and stop the service
param([switch]$Uninstall, [switch]$NoStart)

$taskName = 'GameSwitcher'
$installDir = Split-Path $PSScriptRoot
$exe = Join-Path $installDir 'GameSwitcher.exe'
$startMenuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'GameSwitcher.lnk'

function Stop-Watcher {
    Get-CimInstance Win32_Process -Filter "Name='GameSwitcher.exe'" | Where-Object { $_.CommandLine -like '*--watcher*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
}

# Shortcuts of versions before 2.2 inside the program folder
Remove-Item (Join-Path $installDir 'GameSwitcher.lnk') -ErrorAction SilentlyContinue

if ($Uninstall) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Stop-Watcher
    Remove-Item $startMenuLink -ErrorAction SilentlyContinue
    Write-Host "'$taskName' removed."
    return
}

$build = Join-Path $installDir 'Build.ps1'
if (-not (Test-Path $exe) -and (Test-Path $build)) { & $build | Out-Null }
if (-not (Test-Path $exe)) { throw "GameSwitcher.exe not found in $installDir." }

$action = New-ScheduledTaskAction -Execute $exe -Argument '--watcher' -WorkingDirectory $installDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings `
    -Principal $principal -Description 'Applies the GameSwitcher actions while a selected game is running.' -Force | Out-Null
if (-not $NoStart) { Start-ScheduledTask -TaskName $taskName }

# Start menu entry for the GUI
$lnk = (New-Object -ComObject WScript.Shell).CreateShortcut($startMenuLink)
$lnk.TargetPath = $exe
$lnk.Arguments = ''
$lnk.IconLocation = "$exe,0"
$lnk.WorkingDirectory = $installDir
$lnk.Description = 'GameSwitcher'
$lnk.Save()
Write-Host "'$taskName' installed."
