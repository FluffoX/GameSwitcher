# Checks run by GitHub Actions on every push (and usable locally):
# syntax of the build scripts, translations (complete keys, matching placeholders), text keys used in the
# C# code and PSScriptAnalyzer errors. Compiling (Build.ps1) checks the C# code itself.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$failed = 0

# Syntax of all scripts
foreach ($f in Get-ChildItem $root -Recurse -Filter *.ps1 | Where-Object FullName -notmatch '\\dist\\') {
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$null, [ref]$errors) | Out-Null
    foreach ($e in $errors) { Write-Host "SYNTAX  $($f.Name):$($e.Extent.StartLineNumber)  $($e.Message)"; $failed++ }
}

# Translations: every language has every English key with the same placeholders
$langDir = Join-Path $root 'app\lang'
$en = Get-Content (Join-Path $langDir 'en.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$enKeys = $en.PSObject.Properties.Name
foreach ($f in Get-ChildItem $langDir -Filter *.json) {
    try { $j = Get-Content $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { Write-Host "JSON    $($f.Name)  $($_.Exception.Message)"; $failed++; continue }
    $keys = $j.PSObject.Properties.Name
    foreach ($k in $enKeys) {
        if ($keys -notcontains $k) { Write-Host "MISSING $($f.Name)  $k"; $failed++; continue }
        $a = ([regex]::Matches($en.$k, '\{\d\}') | ForEach-Object Value | Sort-Object) -join ''
        $b = ([regex]::Matches([string]$j.$k, '\{\d\}') | ForEach-Object Value | Sort-Object) -join ''
        if ($a -ne $b) { Write-Host "PLACEHOLDER $($f.Name)  $k"; $failed++ }
    }
}

# Every text key used in the C# code (T("key")) exists in English
$used = Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.cs | Select-String -Pattern '\bT\("([a-zA-Z]+\.[a-zA-Z0-9]+)"' -AllMatches |
    ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
foreach ($k in $used) { if ($enKeys -notcontains $k) { Write-Host "UNKNOWN KEY  $k"; $failed++ } }

# PSScriptAnalyzer (errors only), if the module is available
if (Get-Module -ListAvailable PSScriptAnalyzer) {
    $issues = @(Get-ChildItem $root -Recurse -Filter *.ps1 | Where-Object FullName -notmatch '\\dist\\' |
        ForEach-Object { Invoke-ScriptAnalyzer -Path $_.FullName -Severity Error })
    foreach ($i in $issues) { Write-Host "ANALYZER $($i.ScriptName):$($i.Line)  $($i.RuleName)  $($i.Message)"; $failed++ }
} else {
    Write-Host 'PSScriptAnalyzer not installed - skipped.'
}

if ($failed) { throw "$failed problem(s) found." }
'All checks passed.'
