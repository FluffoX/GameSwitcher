# Settings and profiles for the GUI (the background service reads the same settings.json in C#,
# see src\Config.cs). Also loads the modules the GUI uses for status displays and selection lists.
. (Join-Path $PSScriptRoot 'AppInfo.ps1')
. (Join-Path $PSScriptRoot 'Strings.ps1')
. (Join-Path $PSScriptRoot 'HdrControl.ps1')
. (Join-Path $PSScriptRoot 'AudioControl.ps1')
. (Join-Path $PSScriptRoot 'SystemControl.ps1')

$settingsPath = Join-Path $DataDir 'settings.json'

# Keep in sync with ActionSet.Defaults in src\Config.cs
function New-DefaultActions {
    @{
        Hdr        = @{ Enabled = $true; Revert = $true }
        Sdr        = @{ Enabled = $false; Level = 30; Revert = $true }
        Display    = @{ Enabled = $false; Width = 0; Height = 0; Hz = 0; Revert = $true }
        Monitors   = @{ Enabled = $false; Revert = $true }
        NightLight = @{ Enabled = $false; Revert = $true }
        Audio      = @{ Enabled = $false; DeviceId = ''; DeviceName = ''; Revert = $true }
        Mic        = @{ Enabled = $false; DeviceId = ''; DeviceName = ''; Revert = $true }
        Volume     = @{ Enabled = $false; Level = 50; Revert = $true }
        Power      = @{ Enabled = $false; PlanId = ''; PlanName = ''; Revert = $true }
        KeepAwake  = @{ Enabled = $false }
        CloseApps  = @{ Enabled = $false; Apps = @(); Revert = $true }
        StartApps  = @{ Enabled = $false; Apps = @(); Revert = $true }
        Script     = @{ Enabled = $false; Start = ''; End = '' }
    }
}

# Name/value pairs of a hashtable or a JSON object
function Get-Members($Object) {
    if ($null -eq $Object) { return }
    if ($Object -is [System.Collections.IDictionary]) {
        foreach ($k in @($Object.Keys)) { [pscustomobject]@{ Name = $k; Value = $Object[$k] } }
    } else {
        foreach ($p in $Object.PSObject.Properties) { [pscustomobject]@{ Name = $p.Name; Value = $p.Value } }
    }
}

# A full action set: defaults overlaid with the stored values (also completes profiles from older versions)
function Complete-Actions($Source) {
    $a = New-DefaultActions
    foreach ($action in Get-Members $Source) {
        if (-not $a.ContainsKey($action.Name)) { continue }
        foreach ($field in Get-Members $action.Value) { $a[$action.Name][$field.Name] = $field.Value }
    }
    foreach ($name in 'CloseApps', 'StartApps') { $a[$name].Apps = @($a[$name].Apps | Where-Object { $_ }) }
    $a
}

function Read-Settings {
    try { Get-Content $settingsPath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json } catch { $null }
}
