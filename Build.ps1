# Builds GameSwitcher.exe from src\*.cs.
# Uses the modern C# compiler (Roslyn) from the .NET SDK, but targets the .NET Framework 4.8 that ships
# with Windows 10/11: users need no runtime, and the build needs no internet or packages.
# Version, company and copyright shown in the file properties come from app\AppInfo.ps1.
param([string]$Out = (Join-Path $PSScriptRoot 'GameSwitcher.exe'))
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Roslyn from the newest installed .NET SDK
$dotnet = @((Get-Command dotnet.exe -ErrorAction SilentlyContinue).Source, "$env:ProgramFiles\dotnet\dotnet.exe") |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $dotnet) { throw '.NET SDK not found (https://dotnet.microsoft.com/download).' }
$sdk = & $dotnet --list-sdks | ForEach-Object { if ($_ -match '^(\S+) \[(.+)\]$') { [pscustomobject]@{ Version = [version]($Matches[1] -replace '-.*$', ''); Path = Join-Path $Matches[2] $Matches[1] } } } |
    Sort-Object Version -Descending | Select-Object -First 1
$csc = Join-Path $sdk.Path 'Roslyn\bincore\csc.dll'
if (-not (Test-Path $csc)) { throw "C# compiler not found in $($sdk.Path)." }

# .NET Framework 4.8 assemblies of Windows, and System.Management.Automation of Windows PowerShell 5.1
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = 'mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms', 'System.Web.Extensions', 'System.Management' |
    ForEach-Object { "/reference:$(Join-Path $fw "$_.dll")" }
$refs += "/reference:$([PSObject].Assembly.Location)"

$version = ([regex]::Match((Get-Content (Join-Path $root 'app\AppInfo.ps1') -Raw), "AppVersion = '([\d.]+)'")).Groups[1].Value
if (-not $version) { throw 'AppVersion not found in app\AppInfo.ps1.' }
$versionInfo = Join-Path $env:TEMP "GameSwitcherVersion-$PID.cs"
@"
using System.Reflection;
[assembly: AssemblyTitle("GameSwitcher")]
[assembly: AssemblyProduct("GameSwitcher")]
[assembly: AssemblyDescription("Switches HDR, audio and more when a game starts")]
[assembly: AssemblyCompany("FluffoX")]
[assembly: AssemblyCopyright("Copyright (c) $((Get-Date).Year) FluffoX - MIT License")]
[assembly: AssemblyVersion("$version.0")]
[assembly: AssemblyFileVersion("$version.0")]
[assembly: AssemblyInformationalVersion("$version")]
"@ | Set-Content $versionInfo -Encoding UTF8

try {
    New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
    $sources = Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.cs | ForEach-Object FullName
    $output = & $dotnet $csc /nologo /noconfig /nostdlib+ /target:winexe /optimize+ /langversion:latest /utf8output `
        "/out:$Out" "/win32icon:$(Join-Path $root 'app\icon.ico')" "/win32manifest:$(Join-Path $root 'src\app.manifest')" `
        @refs @sources $versionInfo
    if ($LASTEXITCODE -ne 0) { throw "Build failed:`n$($output -join "`n")" }
    $output | Where-Object { $_ -match 'warning' } | ForEach-Object { Write-Warning $_ }
} finally {
    Remove-Item $versionInfo -ErrorAction SilentlyContinue
}
"Built $Out (version $version)"
