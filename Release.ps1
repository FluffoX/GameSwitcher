# Builds GameSwitcher.exe, packs dist\GameSwitcher-<version>.zip and writes the winget manifests.
#   .\Release.ps1            build ZIP and manifests only
#   .\Release.ps1 -Publish   also create the GitHub release (needs the GitHub CLI "gh")
param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
. (Join-Path $root 'AppInfo.ps1')

$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'stage'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage, (Join-Path $stage 'assets') | Out-Null
# built straight into the package, so a running GameSwitcher.exe does not block the build
& (Join-Path $root 'Build.ps1') -Out (Join-Path $stage 'GameSwitcher.exe') | Out-Null
Copy-Item (Join-Path $root '*.ps1'), (Join-Path $root 'README*.md'), (Join-Path $root 'LICENSE'), (Join-Path $root 'CHANGELOG.md') $stage
Copy-Item (Join-Path $root 'lang'), (Join-Path $root 'src') $stage -Recurse
Copy-Item (Join-Path $root 'assets\icon.ico') (Join-Path $stage 'assets')

$zip = Join-Path $dist "GameSwitcher-$AppVersion.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item $stage -Recurse -Force
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash

# YAML without byte order mark
function Write-Utf8([string]$Path, [Parameter(ValueFromPipeline)][string]$Text) {
    process { [IO.File]::WriteAllText($Path, $Text + "`n", (New-Object Text.UTF8Encoding $false)) }
}

# winget manifests (submit them to github.com/microsoft/winget-pkgs after the release exists)
$id = 'FluffoX.GameSwitcher'
$manifestDir = Join-Path $root "winget\manifests\f\FluffoX\GameSwitcher\$AppVersion"
New-Item -ItemType Directory -Force $manifestDir | Out-Null
$url = "https://github.com/$AppRepo/releases/download/v$AppVersion/GameSwitcher-$AppVersion.zip"
@"
PackageIdentifier: $id
PackageVersion: $AppVersion
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
"@ | Write-Utf8 (Join-Path $manifestDir "$id.yaml")
@"
PackageIdentifier: $id
PackageVersion: $AppVersion
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
  - RelativeFilePath: GameSwitcher.exe
    PortableCommandAlias: gameswitcher
ArchiveBinariesDependOnPath: true
Installers:
  - Architecture: x64
    InstallerUrl: $url
    InstallerSha256: $sha
ManifestType: installer
ManifestVersion: 1.6.0
"@ | Write-Utf8 (Join-Path $manifestDir "$id.installer.yaml")
@"
PackageIdentifier: $id
PackageVersion: $AppVersion
PackageLocale: en-US
Publisher: FluffoX
PublisherUrl: https://github.com/FluffoX
PackageName: GameSwitcher
PackageUrl: https://github.com/$AppRepo
License: MIT
LicenseUrl: https://github.com/$AppRepo/blob/main/LICENSE
ShortDescription: Automatically switches HDR, audio, display mode, power plan and programs when a game starts on Windows 11.
Tags:
  - hdr
  - gaming
  - audio
  - windows-11
  - automation
ReleaseNotesUrl: https://github.com/$AppRepo/releases/tag/v$AppVersion
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@ | Write-Utf8 (Join-Path $manifestDir "$id.locale.en-US.yaml")

"ZIP:       $zip"
"SHA256:    $sha"
"Manifests: $manifestDir"

if ($Publish) {
    $notes = Join-Path $dist 'notes.md'
    $changelog = Get-Content (Join-Path $root 'CHANGELOG.md') -Raw -Encoding UTF8
    $section = [regex]::Match($changelog, "(?s)## $([regex]::Escape($AppVersion))\s*(.+?)(?=\r?\n## |\z)").Groups[1].Value.Trim()
    $section | Write-Utf8 $notes
    gh release create "v$AppVersion" $zip --repo $AppRepo --title "GameSwitcher $AppVersion" --notes-file $notes
}
