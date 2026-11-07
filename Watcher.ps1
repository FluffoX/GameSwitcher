# Runs in the background: applies the actions from Actions.ps1 as soon as a game from games.txt is running,
# and reverts them once all games have exited (depending on the settings in the GUI).
$gamesFile = Join-Path $PSScriptRoot 'games.txt'
$logFile = Join-Path $PSScriptRoot 'watcher.log'
$pollSeconds = 3
$offDelayChecks = 4   # revert only after ~12 s without a game (covers launcher restarts)

function Write-Log([string]$Message) {
    "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $Message" | Add-Content -Path $logFile -Encoding UTF8
}

. (Join-Path $PSScriptRoot 'Actions.ps1')

# Allow only one instance
$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\GameSwitcherWatcher', [ref]$createdNew)
if (-not $createdNew) { exit }

# Keep the log small
if ((Test-Path $logFile) -and (Get-Item $logFile).Length -gt 256KB) {
    $tail = Get-Content $logFile -Tail 500 -Encoding UTF8
    Set-Content $logFile $tail -Encoding UTF8
}

Write-Log 'Watcher gestartet.'
$active = $false
$idleChecks = 0

while ($true) {
    try {
        # Re-read the file every time so changes apply without a restart
        $games = @()
        if (Test-Path $gamesFile) {
            $games = Get-Content $gamesFile -Encoding UTF8 |
                ForEach-Object { ($_ -replace '#.*$', '').Trim() -replace '\.exe$', '' } |
                Where-Object { $_ }
        }
        $running = if ($games) { Get-Process -Name $games -ErrorAction SilentlyContinue | Select-Object -First 1 }

        if ($running) {
            $idleChecks = 0
            if (-not $active) {
                $active = $true
                Write-Log "$($running.ProcessName) gestartet"
                Invoke-GameStart
            }
        }
        elseif ($active) {
            $idleChecks++
            if ($idleChecks -ge $offDelayChecks) {
                Write-Log 'Kein Spiel mehr aktiv'
                Invoke-GameEnd
                $active = $false
                $idleChecks = 0
            }
        }
    }
    catch {
        Write-Log "Fehler: $($_.Exception.Message)"
    }
    Start-Sleep -Seconds $pollSeconds
}
