# Builds the release files into dist\:
#   GameSwitcher-Setup-<version>.exe   installer (needs Inno Setup 6)
#   GameSwitcher-<version>.zip         portable version
#   SHA256SUMS.txt                     checksums of both
#   notes.md                           release notes from CHANGELOG.md
# Used by the GitHub Actions release workflow; works the same locally.
#   .\Release.ps1                 build
#   .\Release.ps1 -Tag v2.2.0     build and check that the tag matches the app version
param([string]$Tag)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = ([regex]::Match((Get-Content (Join-Path $root 'src\AppInfo.cs') -Raw), 'Version = "([\d.]+)"')).Groups[1].Value
if ($Tag -and $Tag -ne "v$version") { throw "Tag $Tag does not match version $version in src\AppInfo.cs." }

function Write-Utf8([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding $false)) }

$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'stage'
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null

# What users get: GameSwitcher.exe, the app folder and the documents - no build tools
& (Join-Path $root 'Build.ps1') -Out (Join-Path $stage 'GameSwitcher.exe') | Out-Null
Copy-Item (Join-Path $root 'app') $stage -Recurse
Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'README.de.md'), (Join-Path $root 'LICENSE'), (Join-Path $root 'CHANGELOG.md') $stage

# Portable ZIP
$zip = Join-Path $dist "GameSwitcher-$version.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip

# Installer
$iscc = @((Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if ($iscc) {
    $output = & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$stage" "/DOutputDir=$dist" (Join-Path $root 'installer\GameSwitcher.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed:`n$($output -join "`n")" }
} elseif ($Tag) {
    throw 'Inno Setup (ISCC.exe) not found.'
} else {
    Write-Warning 'Inno Setup not found - building the ZIP only.'
}
Remove-Item $stage -Recurse -Force

# Checksums and release notes
$sums = Get-ChildItem $dist -File | Where-Object Extension -in '.exe', '.zip' | Sort-Object Name |
    ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)" }
Write-Utf8 (Join-Path $dist 'SHA256SUMS.txt') (($sums -join "`n") + "`n")
$changelog = Get-Content (Join-Path $root 'CHANGELOG.md') -Raw -Encoding UTF8
$section = [regex]::Match($changelog, "(?s)## $([regex]::Escape($version))\s*(.+?)(?=\r?\n## |\z)").Groups[1].Value.Trim()
Write-Utf8 (Join-Path $dist 'notes.md') ($section + "`n`n**Installation:** download ``GameSwitcher-Setup-$version.exe`` and run it. Prefer no installation? Use ``GameSwitcher-$version.zip``, extract it anywhere and start ``GameSwitcher.exe``.`n")

Get-ChildItem $dist -File | ForEach-Object { "{0,-34} {1,10:N0} bytes" -f $_.Name, $_.Length }
