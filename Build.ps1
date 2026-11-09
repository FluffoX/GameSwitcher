# Builds GameSwitcher.exe from src\Launcher.cs with the C# compiler included in Windows (.NET Framework 4).
param([string]$Out = (Join-Path $PSScriptRoot 'GameSwitcher.exe'))
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'C# compiler (.NET Framework 4) not found.' }

# System.Management.Automation.dll of Windows PowerShell 5.1
$sma = [PSObject].Assembly.Location
$output = & $csc /nologo /target:winexe /optimize+ "/out:$Out" "/win32icon:$(Join-Path $root 'assets\icon.ico')" `
    "/reference:$sma" /reference:System.Windows.Forms.dll (Join-Path $root 'src\Launcher.cs')
if ($LASTEXITCODE -ne 0) { throw "Build failed:`n$($output -join "`n")" }
"Built $Out"
