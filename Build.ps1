# Builds GameSwitcher.exe from src\Launcher.cs with the C# compiler included in Windows (.NET Framework 4).
# Version, company and copyright shown in the file properties come from app\AppInfo.ps1.
param([string]$Out = (Join-Path $PSScriptRoot 'GameSwitcher.exe'))
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'C# compiler (.NET Framework 4) not found.' }

$version = ([regex]::Match((Get-Content (Join-Path $root 'app\AppInfo.ps1') -Raw), "AppVersion = '([\d.]+)'")).Groups[1].Value
if (-not $version) { throw 'AppVersion not found in app\AppInfo.ps1.' }
$versionInfo = Join-Path $env:TEMP "GameSwitcherVersion-$PID.cs"
@"
using System.Reflection;
[assembly: AssemblyVersion("$version.0")]
[assembly: AssemblyFileVersion("$version.0")]
[assembly: AssemblyInformationalVersion("$version")]
[assembly: AssemblyCompany("FluffoX")]
[assembly: AssemblyCopyright("Copyright (c) $((Get-Date).Year) FluffoX - MIT License")]
"@ | Set-Content $versionInfo -Encoding UTF8

try {
    # System.Management.Automation.dll of Windows PowerShell 5.1
    $sma = [PSObject].Assembly.Location
    $output = & $csc /nologo /target:winexe /optimize+ "/out:$Out" "/win32icon:$(Join-Path $root 'app\icon.ico')" `
        "/reference:$sma" /reference:System.Windows.Forms.dll (Join-Path $root 'src\Launcher.cs') $versionInfo
    if ($LASTEXITCODE -ne 0) { throw "Build failed:`n$($output -join "`n")" }
} finally {
    Remove-Item $versionInfo -ErrorAction SilentlyContinue
}
"Built $Out (version $version)"
