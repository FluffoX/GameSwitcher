# Writes the winget manifests for a published release (installer from GitHub, checksum computed
# from the downloaded file). Submit the folder to github.com/microsoft/winget-pkgs afterwards.
#   .\tools\New-WingetManifest.ps1 -Version 2.2.0
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$repo = 'FluffoX/GameSwitcher'
$id = 'FluffoX.GameSwitcher'
$url = "https://github.com/$repo/releases/download/v$Version/GameSwitcher-Setup-$Version.exe"

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$tmp = Join-Path $env:TEMP "GameSwitcher-Setup-$Version.exe"
Invoke-WebRequest $url -OutFile $tmp -UseBasicParsing
$sha = (Get-FileHash $tmp -Algorithm SHA256).Hash
Remove-Item $tmp

$dir = Join-Path $root "winget\manifests\f\FluffoX\GameSwitcher\$Version"
New-Item -ItemType Directory -Force $dir | Out-Null
function Write-Yaml([string]$Name, [string]$Text) { [IO.File]::WriteAllText((Join-Path $dir $Name), $Text, (New-Object Text.UTF8Encoding $false)) }

Write-Yaml "$id.yaml" @"
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0

"@
Write-Yaml "$id.installer.yaml" @"
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: inno
Scope: user
UpgradeBehavior: install
Installers:
  - Architecture: x64
    InstallerUrl: $url
    InstallerSha256: $sha
ManifestType: installer
ManifestVersion: 1.6.0

"@
Write-Yaml "$id.locale.en-US.yaml" @"
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: FluffoX
PublisherUrl: https://github.com/FluffoX
PackageName: GameSwitcher
PackageUrl: https://github.com/$repo
License: MIT
LicenseUrl: https://github.com/$repo/blob/main/LICENSE
ShortDescription: Automatically switches HDR, audio, display mode, power plan and programs when a game starts on Windows 11.
Tags:
  - hdr
  - gaming
  - audio
  - windows-11
  - automation
ReleaseNotesUrl: https://github.com/$repo/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: 1.6.0

"@
"Manifests written to $dir (SHA256 $sha)"
