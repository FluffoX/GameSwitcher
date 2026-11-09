# App metadata and folders shared by all scripts
$AppVersion = '2.3.0'
$AppRepo = 'FluffoX/GameSwitcher'

# Program folder (GameSwitcher.exe) is the parent of this app folder
$InstallDir = Split-Path $PSScriptRoot

# Settings, play time, state and logs live in the user's profile, so the program folder can be
# replaced by an update (or sit in a read-only location) without losing anything.
# GAMESWITCHER_DATA can point somewhere else (tests, portable use); same rule as src\Paths.cs.
$DataDir = if ($env:GAMESWITCHER_DATA) { $env:GAMESWITCHER_DATA } else { Join-Path $env:APPDATA 'GameSwitcher' }
if (-not (Test-Path $DataDir)) { New-Item -ItemType Directory -Force $DataDir | Out-Null }

# Up to version 2.1 these files were kept next to GameSwitcher.exe: move them over once
foreach ($name in 'settings.json', 'playtime.json', 'state.json', 'pause.json', 'watcher.log', 'error.log') {
    $old = Join-Path $InstallDir $name
    $new = Join-Path $DataDir $name
    if ((Test-Path $old) -and -not (Test-Path $new)) {
        try { Move-Item $old $new -ErrorAction Stop } catch {}
    }
}
