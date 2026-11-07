# GameSwitcher GUI. Start it without a console window via the GameSwitcher shortcut.
# Finds installed games automatically and writes the selected ones to games.txt,
# which the watcher picks up immediately.
# Test without a window:  .\GameSwitcher.ps1 -ScanOnly
param([switch]$ScanOnly)

$root = $PSScriptRoot
$settingsFile = Join-Path $root 'settings.json'
$gamesFile = Join-Path $root 'games.txt'
$logFile = Join-Path $root 'watcher.log'
$taskName = 'GameSwitcher'

# Folders and executables that are certainly not games
$skipDirs = '^(_?commonredist|redist|redistributables?|directx|dotnetfx|vcredist|easyanticheat|battleye|__installer|installers?|support|prereq.*|\$.*)$'
$skipExe = '(unins|setup|install|redist|crash|report|dxsetup|easyanticheat|beservice|battleye|prereq|cefprocess|helper|updater|update|webview|dotnet|vulkan|physx|oalinst|benchmark|server|anticheat|overlay|injector|activationui|debugger|bssndrpt|checkenv|worldbuilder|^cdb$|^7z|^8za)'

#region Game discovery

function Find-GameExes([string]$Dir, [int]$Depth = 0) {
    $result = @()
    try {
        foreach ($f in [IO.Directory]::EnumerateFiles($Dir, '*.exe')) {
            if ([IO.Path]::GetFileNameWithoutExtension($f) -notmatch $skipExe) { $result += $f }
        }
        if ($Depth -lt 5) {
            foreach ($d in [IO.Directory]::EnumerateDirectories($Dir)) {
                if ((Split-Path $d -Leaf) -notmatch $skipDirs) { $result += Find-GameExes $d ($Depth + 1) }
            }
        }
    } catch {}
    $result
}

function New-Game([string]$Name, [string]$Source, [string]$Path, [string]$Origin) {
    $exes = @(Find-GameExes $Path | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object -Unique)
    if ($exes.Count -eq 0) { return }
    [pscustomobject]@{ Name = $Name; Source = $Source; Path = $Path; Key = $Path.TrimEnd('\').ToLower(); Exes = $exes; Origin = $Origin }
}

function New-ExeGame([string]$Exe) {
    $name = [Diagnostics.FileVersionInfo]::GetVersionInfo($Exe).ProductName
    if (-not $name) { $name = [IO.Path]::GetFileNameWithoutExtension($Exe) }
    [pscustomobject]@{ Name = $name.Trim(); Source = 'Custom'; Path = $Exe; Key = $Exe.ToLower(); Exes = @([IO.Path]::GetFileName($Exe)); Origin = $Exe }
}

function Get-SteamGames {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { $steam = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -ErrorAction SilentlyContinue).InstallPath }
    if (-not $steam) { return }
    $steam = $steam -replace '/', '\'
    $libs = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        $libs += Select-String -Path $vdf -Pattern '"path"\s+"(.+?)"' | ForEach-Object { $_.Matches[0].Groups[1].Value -replace '\\\\', '\' }
    }
    foreach ($lib in $libs | ForEach-Object { $_.TrimEnd('\') } | Sort-Object -Unique) {
        foreach ($acf in Get-ChildItem (Join-Path $lib 'steamapps') -Filter 'appmanifest_*.acf' -ErrorAction SilentlyContinue) {
            $text = Get-Content $acf.FullName -Raw -Encoding UTF8
            $name = [regex]::Match($text, '"name"\s+"(.+?)"').Groups[1].Value
            $dir = [regex]::Match($text, '"installdir"\s+"(.+?)"').Groups[1].Value
            if (-not $dir -or $name -match '^(Steamworks Common Redistributables|Proton|Steam Linux Runtime|SteamVR)') { continue }
            $path = Join-Path $lib "steamapps\common\$dir"
            if (Test-Path $path) { New-Game $name 'Steam' $path 'Steam' }
        }
    }
}

function Get-EpicGames {
    $dir = Join-Path $env:ProgramData 'Epic\EpicGamesLauncher\Data\Manifests'
    foreach ($f in Get-ChildItem $dir -Filter '*.item' -ErrorAction SilentlyContinue) {
        try { $m = Get-Content $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } catch { continue }
        if ($m.bIsIncompleteInstall) { continue }
        if ($m.AppCategories -and ($m.AppCategories -notcontains 'games')) { continue }
        if ($m.InstallLocation -and (Test-Path $m.InstallLocation)) { New-Game $m.DisplayName 'Epic' $m.InstallLocation 'Epic' }
    }
}

function Get-GogGames {
    foreach ($k in Get-ChildItem 'HKLM:\SOFTWARE\WOW6432Node\GOG.com\Games' -ErrorAction SilentlyContinue) {
        $p = Get-ItemProperty $k.PSPath
        if ($p.path -and (Test-Path $p.path)) { New-Game $p.gameName 'GOG' $p.path 'GOG' }
    }
}

function Get-UbisoftGames {
    foreach ($k in Get-ChildItem 'HKLM:\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs' -ErrorAction SilentlyContinue) {
        $p = (Get-ItemProperty $k.PSPath).InstallDir
        if ($p) {
            $p = ($p -replace '/', '\').TrimEnd('\')
            if (Test-Path $p) { New-Game (Split-Path $p -Leaf) 'Ubisoft' $p 'Ubisoft' }
        }
    }
}

# Every subfolder is a game; if executables sit directly in the folder, the folder itself is the game.
function Get-FolderGames([string]$Folder, [string]$Source) {
    if (-not (Test-Path $Folder)) { return }
    $direct = @([IO.Directory]::EnumerateFiles($Folder, '*.exe') | Where-Object { [IO.Path]::GetFileNameWithoutExtension($_) -notmatch $skipExe })
    if ($direct.Count -gt 0) { return New-Game (Split-Path $Folder -Leaf) $Source $Folder $Folder }
    foreach ($d in [IO.Directory]::EnumerateDirectories($Folder)) {
        if ((Split-Path $d -Leaf) -notmatch $skipDirs) { New-Game (Split-Path $d -Leaf) $Source $d $Folder }
    }
}

function Get-XboxGames {
    foreach ($drive in Get-PSDrive -PSProvider FileSystem) {
        $dir = Join-Path $drive.Root 'XboxGames'
        if (Test-Path $dir) { Get-FolderGames $dir 'Xbox' }
    }
}

#endregion

#region Settings

$selected = @{}                 # Key -> @{Key; Name; Exes}
$customFolders = New-Object System.Collections.ArrayList
$customExes = New-Object System.Collections.ArrayList
$games = @()

. (Join-Path $root 'Actions.ps1')
$actions = Get-ActionSettings
Initialize-Language (Get-ConfiguredLanguage)

if (Test-Path $settingsFile) {
    try {
        $s = Get-Content $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($e in $s.Selected) { $selected[$e.Key] = [pscustomobject]@{ Key = $e.Key; Name = $e.Name; Exes = @($e.Exes) } }
        foreach ($f in $s.CustomFolders) { [void]$customFolders.Add($f) }
        foreach ($e in $s.CustomExes) { [void]$customExes.Add($e) }
    } catch {}
}

function Save-All {
    @{ Selected = @($selected.Values); CustomFolders = @($customFolders); CustomExes = @($customExes)
       Actions = $script:actions; Language = $script:language } |
        ConvertTo-Json -Depth 6 | Set-Content $settingsFile -Encoding UTF8
    $lines = @('# Managed by GameSwitcher.ps1 - manual changes will be overwritten.')
    foreach ($g in $selected.Values | Sort-Object Name) { $lines += "# $($g.Name)"; $lines += $g.Exes }
    Set-Content $gamesFile $lines -Encoding UTF8
}

function Find-AllGames {
    $all = @(Get-SteamGames) + @(Get-EpicGames) + @(Get-GogGames) + @(Get-UbisoftGames) + @(Get-XboxGames) +
        @(Get-FolderGames (Join-Path $env:ProgramFiles 'EA Games') 'EA')
    foreach ($f in $customFolders) { $all += @(Get-FolderGames $f 'Folder') }
    foreach ($e in $customExes) { if (Test-Path $e) { $all += New-ExeGame $e } }

    $found = [ordered]@{}
    foreach ($g in $all) { if ($g -and -not $found.Contains($g.Key)) { $found[$g.Key] = $g } }
    foreach ($s in @($selected.Values)) {
        if ($found.Contains($s.Key)) {
            $s.Exes = $found[$s.Key].Exes   # refresh the executable list after game updates
        } else {
            $found[$s.Key] = [pscustomobject]@{ Name = $s.Name; Source = 'Missing'; Path = ''; Key = $s.Key; Exes = @($s.Exes); Origin = $null }
        }
    }
    $script:games = @($found.Values | Sort-Object Name)
}

#endregion

if ($ScanOnly) {
    Find-AllGames
    $games | Format-Table Name, Source, @{ n = 'Processes'; e = { $_.Exes -join ', ' } } -AutoSize -Wrap
    return
}

#region Window

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -TypeDefinition @'
public class AppEntry { public string Path; public string Name; public override string ToString() { return Name; } }
public class LangItem { public string Code; public string Name; public override string ToString() { return Name; } }
'@
Add-Type -Name Dpi -Namespace Native -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();'
[void][Native.Dpi]::SetProcessDPIAware()
[System.Windows.Forms.Application]::EnableVisualStyles()

$colorOn = [System.Drawing.Color]::FromArgb(0, 150, 70)
$colorOff = [System.Drawing.Color]::Gray
$removableSources = 'Custom', 'Folder', 'Missing'

$form = New-Object System.Windows.Forms.Form
$form.Text = 'GameSwitcher'
$form.Font = New-Object System.Drawing.Font('Segoe UI', 9)
$g = $form.CreateGraphics(); $scale = $g.DpiX / 96; $g.Dispose()
$form.ClientSize = New-Object System.Drawing.Size([int](1240 * $scale), [int](700 * $scale))
$form.StartPosition = 'CenterScreen'

$list = New-Object System.Windows.Forms.ListView
$list.Dock = 'Fill'; $list.View = 'Details'; $list.CheckBoxes = $true; $list.FullRowSelect = $true
$list.HideSelection = $false; $list.MultiSelect = $false; $list.ShowItemToolTips = $true
[void]$list.Columns.Add('', [int](320 * $scale))
[void]$list.Columns.Add('', [int](110 * $scale))
[void]$list.Columns.Add('', [int](340 * $scale))

# Top: current HDR state
$hdrPanel = New-Object System.Windows.Forms.FlowLayoutPanel
$hdrPanel.Dock = 'Top'; $hdrPanel.AutoSize = $true; $hdrPanel.Padding = '6,8,6,2'
$lblHdr = New-Object System.Windows.Forms.Label
$lblHdr.AutoSize = $true; $lblHdr.Margin = '3,4,12,3'
$lblHdr.Font = New-Object System.Drawing.Font('Segoe UI', 11, [System.Drawing.FontStyle]::Bold)
$btnToggleHdr = New-Object System.Windows.Forms.Button
$btnToggleHdr.AutoSize = $true
$lblRunning = New-Object System.Windows.Forms.Label
$lblRunning.AutoSize = $true; $lblRunning.Margin = '18,8,3,3'
$lblAudio = New-Object System.Windows.Forms.Label
$lblAudio.AutoSize = $true; $lblAudio.Margin = '18,8,3,3'
$hdrPanel.Controls.AddRange(@($lblHdr, $btnToggleHdr, $lblRunning, $lblAudio))

$top = New-Object System.Windows.Forms.FlowLayoutPanel
$top.Dock = 'Top'; $top.AutoSize = $true; $top.Padding = '6,2,6,2'
$lblSearch = New-Object System.Windows.Forms.Label
$lblSearch.AutoSize = $true; $lblSearch.Margin = '3,7,3,3'
$txtSearch = New-Object System.Windows.Forms.TextBox
$txtSearch.Width = [int](260 * $scale)
$chkOnlySelected = New-Object System.Windows.Forms.CheckBox
$chkOnlySelected.AutoSize = $true; $chkOnlySelected.Margin = '12,5,3,3'
$btnRescan = New-Object System.Windows.Forms.Button
$btnRescan.AutoSize = $true
$top.Controls.AddRange(@($lblSearch, $txtSearch, $chkOnlySelected, $btnRescan))

# Bottom: edit the list
$bottom = New-Object System.Windows.Forms.FlowLayoutPanel
$bottom.Dock = 'Bottom'; $bottom.AutoSize = $true; $bottom.Padding = '6,4,6,0'
$btnAddExe = New-Object System.Windows.Forms.Button
$btnAddExe.AutoSize = $true
$btnAddFolder = New-Object System.Windows.Forms.Button
$btnAddFolder.AutoSize = $true
$btnRemove = New-Object System.Windows.Forms.Button
$btnRemove.AutoSize = $true; $btnRemove.Enabled = $false
$btnLog = New-Object System.Windows.Forms.Button
$btnLog.AutoSize = $true; $btnLog.Margin = '24,3,3,3'
$bottom.Controls.AddRange(@($btnAddExe, $btnAddFolder, $btnRemove, $btnLog))

# Bottom: options, service and language
$options = New-Object System.Windows.Forms.FlowLayoutPanel
$options.Dock = 'Bottom'; $options.AutoSize = $true; $options.Padding = '6,0,6,6'
$chkAutostart = New-Object System.Windows.Forms.CheckBox
$chkAutostart.AutoSize = $true; $chkAutostart.Margin = '3,7,12,3'
$btnService = New-Object System.Windows.Forms.Button
$btnService.AutoSize = $true
$lblLanguage = New-Object System.Windows.Forms.Label
$lblLanguage.AutoSize = $true; $lblLanguage.Margin = '36,8,3,3'
$cmbLanguage = New-Object System.Windows.Forms.ComboBox
$cmbLanguage.DropDownStyle = 'DropDownList'; $cmbLanguage.Width = [int](170 * $scale); $cmbLanguage.Margin = '3,5,3,3'
$options.Controls.AddRange(@($chkAutostart, $btnService, $lblLanguage, $cmbLanguage))

# Right: actions on game start
$actionsPanel = New-Object System.Windows.Forms.FlowLayoutPanel
$actionsPanel.Dock = 'Right'; $actionsPanel.Width = [int](400 * $scale)
$actionsPanel.FlowDirection = 'TopDown'; $actionsPanel.WrapContents = $false; $actionsPanel.AutoScroll = $true
$actionsPanel.Padding = '10,4,10,4'
$innerWidth = [int](350 * $scale)

function New-Check([int]$Indent = 0, [int]$Top = 2) {
    $c = New-Object System.Windows.Forms.CheckBox
    $c.AutoSize = $true; $c.Margin = "$(3 + $Indent),$Top,3,1"
    $c.MaximumSize = New-Object System.Drawing.Size(($innerWidth - $Indent), 0)
    $c
}

function New-AppList {
    $l = New-Object System.Windows.Forms.ListBox
    $l.Width = $innerWidth - [int](20 * $scale); $l.Height = [int](64 * $scale)
    $l.Margin = '23,3,3,2'; $l.HorizontalScrollbar = $true
    $l
}

function New-ButtonRow {
    $row = New-Object System.Windows.Forms.FlowLayoutPanel
    $row.AutoSize = $true; $row.WrapContents = $false; $row.Margin = '20,0,0,0'
    foreach ($i in 1..2) {
        $b = New-Object System.Windows.Forms.Button
        $b.AutoSize = $true
        [void]$row.Controls.Add($b)
    }
    $row
}

$lblActions = New-Object System.Windows.Forms.Label
$lblActions.AutoSize = $true; $lblActions.Margin = '3,6,3,4'
$lblActions.Font = New-Object System.Drawing.Font('Segoe UI', 10, [System.Drawing.FontStyle]::Bold)

$chkHdr = New-Check 0 6
$chkHdrRevert = New-Check 20

$chkAudio = New-Check 0 16
$cmbAudio = New-Object System.Windows.Forms.ComboBox
$cmbAudio.DropDownStyle = 'DropDownList'; $cmbAudio.Width = $innerWidth - [int](20 * $scale); $cmbAudio.Margin = '23,3,3,2'
$chkAudioRevert = New-Check 20

$chkClose = New-Check 0 16
$lstClose = New-AppList
$closeRow = New-ButtonRow
$btnCloseAdd, $btnCloseRemove = $closeRow.Controls[0], $closeRow.Controls[1]
$chkCloseRevert = New-Check 20

$chkStart = New-Check 0 16
$lstStart = New-AppList
$startRow = New-ButtonRow
$btnStartAdd, $btnStartRemove = $startRow.Controls[0], $startRow.Controls[1]
$chkStartRevert = New-Check 20

$actionsPanel.Controls.AddRange(@(
    $lblActions,
    $chkHdr, $chkHdrRevert,
    $chkAudio, $cmbAudio, $chkAudioRevert,
    $chkClose, $lstClose, $closeRow, $chkCloseRevert,
    $chkStart, $lstStart, $startRow, $chkStartRevert))

# Menu for adding programs
$addMenu = New-Object System.Windows.Forms.ContextMenuStrip
$miAddRunning = $addMenu.Items.Add('')
$miAddFile = $addMenu.Items.Add('')
$script:addTarget = $null

$statusBar = New-Object System.Windows.Forms.StatusStrip
$status = New-Object System.Windows.Forms.ToolStripStatusLabel
[void]$statusBar.Items.Add($status)

# Context menu of the list
$menu = New-Object System.Windows.Forms.ContextMenuStrip
$miOpen = $menu.Items.Add('')
$miRemove = $menu.Items.Add('')
$list.ContextMenuStrip = $menu

# Order matters: add the Fill control first and the outermost bars last
$form.Controls.AddRange(@($list, $actionsPanel, $top, $hdrPanel, $bottom, $options, $statusBar))

# Let buttons shrink again when switching to a language with shorter texts
function Set-ButtonSizing($Parent) {
    foreach ($c in $Parent.Controls) {
        if ($c -is [System.Windows.Forms.Button]) {
            $c.AutoSizeMode = 'GrowAndShrink'
            $c.MinimumSize = New-Object System.Drawing.Size([int](80 * $scale), [int](26 * $scale))
            $c.Padding = '6,0,6,0'
        }
        Set-ButtonSizing $c
    }
}
Set-ButtonSizing $form

$script:loading = $false
$script:serviceRunning = $false
$script:hdrDisplays = @()
$script:tick = 0

# Static texts in the current language; dynamic texts are set by the Update-* functions
function Set-UiTexts {
    # Languages with their own script (e.g. CJK) can name a matching Windows font via "_font"
    $fontName = $script:strings['_font']
    if (-not $fontName) { $fontName = 'Segoe UI' }
    $form.Font = New-Object System.Drawing.Font($fontName, 9)
    $lblHdr.Font = New-Object System.Drawing.Font($fontName, 11, [System.Drawing.FontStyle]::Bold)
    $lblActions.Font = New-Object System.Drawing.Font($fontName, 10, [System.Drawing.FontStyle]::Bold)

    $list.Columns[0].Text = T 'col.game'
    $list.Columns[1].Text = T 'col.source'
    $list.Columns[2].Text = T 'col.processes'
    $lblSearch.Text = T 'search.label'
    $chkOnlySelected.Text = T 'search.onlySelected'
    $btnRescan.Text = T 'btn.rescan'
    $btnAddExe.Text = T 'btn.addExe'
    $btnAddFolder.Text = T 'btn.addFolder'
    $btnRemove.Text = T 'btn.remove'
    $btnLog.Text = T 'btn.log'
    $chkAutostart.Text = T 'opt.autostart'
    $lblLanguage.Text = T 'language.label'
    $lblActions.Text = T 'actions.title'
    $chkHdr.Text = T 'act.hdr'
    $chkHdrRevert.Text = T 'act.hdrRevert'
    $chkAudio.Text = T 'act.audio'
    $chkAudioRevert.Text = T 'act.audioRevert'
    $chkClose.Text = T 'act.close'
    $chkCloseRevert.Text = T 'act.closeRevert'
    $chkStart.Text = T 'act.start'
    $chkStartRevert.Text = T 'act.startRevert'
    foreach ($b in $btnCloseAdd, $btnStartAdd) { $b.Text = T 'btn.add' }
    foreach ($b in $btnCloseRemove, $btnStartRemove) { $b.Text = T 'btn.remove' }
    $miAddRunning.Text = T 'menu.addRunning'
    $miAddFile.Text = T 'menu.addFile'
    $miOpen.Text = T 'menu.openLocation'
    $miRemove.Text = T 'menu.removeFromList'
}

function Get-SourceText([string]$Source) {
    switch ($Source) {
        'Custom' { T 'source.custom' }
        'Folder' { T 'source.folder' }
        'Missing' { T 'source.missing' }
        default { $Source }
    }
}

function Get-WatcherProcesses {
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -like '*Watcher.ps1*' }
}

function Update-ServiceState {
    $script:serviceRunning = [bool](Get-WatcherProcesses)
    $btnService.Text = if ($script:serviceRunning) { T 'btn.serviceStop' } else { T 'btn.serviceStart' }
    $script:loading = $true
    $chkAutostart.Checked = [bool](Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)
    $script:loading = $false
}

function Update-Status {
    $service = if ($script:serviceRunning) { T 'service.running' } else { T 'service.stopped' }
    $status.Text = T 'status.summary' $selected.Count $games.Count $service
}

function Update-Hdr {
    try { $script:hdrDisplays = @(Get-HdrDisplays | Where-Object HdrSupported) } catch { $script:hdrDisplays = @() }
    $displays = $script:hdrDisplays
    if ($displays.Count -eq 0) {
        $lblHdr.Text = '● ' + (T 'hdr.none')
        $lblHdr.ForeColor = $colorOff
        $btnToggleHdr.Text = T 'btn.hdrOn'
        $btnToggleHdr.Enabled = $false
        return
    }
    $on = @($displays | Where-Object HdrEnabled)
    $state = if ($on.Count -eq $displays.Count) { T 'hdr.on' } elseif ($on.Count) { T 'hdr.partial' } else { T 'hdr.off' }
    $names = if ($displays.Count -eq 1) { $displays[0].Name } else {
        ($displays | ForEach-Object { "$($_.Name): $(if ($_.HdrEnabled) { T 'hdr.displayOn' } else { T 'hdr.displayOff' })" }) -join ', '
    }
    $lblHdr.Text = "● $state  –  $names"
    $lblHdr.ForeColor = if ($on.Count) { $colorOn } else { $colorOff }
    $btnToggleHdr.Enabled = $true
    $btnToggleHdr.Text = if ($on.Count) { T 'btn.hdrOff' } else { T 'btn.hdrOn' }
}

function Update-Running {
    $map = @{}
    foreach ($s in $selected.Values) { foreach ($x in $s.Exes) { $map[($x -replace '\.exe$', '').ToLower()] = $s.Name } }
    $names = @()
    if ($map.Count) {
        $names = @(Get-Process -Name @($map.Keys) -ErrorAction SilentlyContinue |
            ForEach-Object { $map[$_.ProcessName.ToLower()] } | Sort-Object -Unique)
    }
    $lblRunning.Text = if ($names) { T 'running.games' ($names -join ', ') } else { T 'running.none' }
}

function Update-AudioLabel {
    try { $name = [AudioNative]::GetDefaultOutputName() } catch { $name = $null }
    $lblAudio.Text = T 'audio.current' $(if ($name) { $name } else { '–' })
}

#region Actions

function New-AppEntry([string]$Path) {
    $entry = New-Object AppEntry
    $entry.Path = $Path
    $desc = $null
    try { $desc = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileDescription } catch {}
    $file = [IO.Path]::GetFileName($Path)
    $entry.Name = if ($desc -and $desc.Trim()) { "$($desc.Trim())  ($file)" } else { $file }
    $entry
}

function Update-AudioDeviceList {
    $script:loading = $true
    $cmbAudio.Items.Clear()
    $devices = @()
    try { $devices = @([AudioNative]::GetOutputDevices()) } catch {}
    foreach ($d in $devices) { [void]$cmbAudio.Items.Add($d) }
    $id = $actions.Audio.DeviceId
    if ($id) {
        $match = $devices | Where-Object Id -eq $id | Select-Object -First 1
        if (-not $match) {
            $match = New-Object AudioDevice
            $match.Id = $id; $match.Disconnected = $true
            $match.Name = T 'audio.disconnected' $actions.Audio.DeviceName
            [void]$cmbAudio.Items.Add($match)
        }
        $cmbAudio.SelectedItem = $match
    }
    $script:loading = $false
}

function Update-ActionControls {
    $chkHdrRevert.Enabled = $chkHdr.Checked
    $cmbAudio.Enabled = $chkAudio.Checked
    $chkAudioRevert.Enabled = $chkAudio.Checked
    foreach ($c in $lstClose, $btnCloseAdd, $btnCloseRemove, $chkCloseRevert) { $c.Enabled = $chkClose.Checked }
    foreach ($c in $lstStart, $btnStartAdd, $btnStartRemove, $chkStartRevert) { $c.Enabled = $chkStart.Checked }
}

function Show-Actions {
    $script:loading = $true
    $chkHdr.Checked = [bool]$actions.Hdr.Enabled; $chkHdrRevert.Checked = [bool]$actions.Hdr.Revert
    $chkAudio.Checked = [bool]$actions.Audio.Enabled; $chkAudioRevert.Checked = [bool]$actions.Audio.Revert
    $chkClose.Checked = [bool]$actions.CloseApps.Enabled; $chkCloseRevert.Checked = [bool]$actions.CloseApps.Revert
    $chkStart.Checked = [bool]$actions.StartApps.Enabled; $chkStartRevert.Checked = [bool]$actions.StartApps.Revert
    $lstClose.Items.Clear(); foreach ($p in $actions.CloseApps.Apps) { [void]$lstClose.Items.Add((New-AppEntry $p)) }
    $lstStart.Items.Clear(); foreach ($p in $actions.StartApps.Apps) { [void]$lstStart.Items.Add((New-AppEntry $p)) }
    $script:loading = $false
    Update-AudioDeviceList
    Update-ActionControls
}

function Save-Actions {
    if ($script:loading) { return }
    $actions.Hdr.Enabled = $chkHdr.Checked; $actions.Hdr.Revert = $chkHdrRevert.Checked
    $actions.Audio.Enabled = $chkAudio.Checked; $actions.Audio.Revert = $chkAudioRevert.Checked
    $dev = $cmbAudio.SelectedItem
    if ($dev) {
        $actions.Audio.DeviceId = $dev.Id
        if (-not $dev.Disconnected) { $actions.Audio.DeviceName = $dev.Name }
    }
    $actions.CloseApps.Enabled = $chkClose.Checked; $actions.CloseApps.Revert = $chkCloseRevert.Checked
    $actions.CloseApps.Apps = @($lstClose.Items | ForEach-Object { $_.Path })
    $actions.StartApps.Enabled = $chkStart.Checked; $actions.StartApps.Revert = $chkStartRevert.Checked
    $actions.StartApps.Apps = @($lstStart.Items | ForEach-Object { $_.Path })
    Update-ActionControls
    Save-All
}

function Add-AppToList([System.Windows.Forms.ListBox]$ListBox, [string]$Path) {
    if (-not $Path) { return }
    if (@($ListBox.Items | Where-Object { $_.Path -eq $Path }).Count) { return }
    [void]$ListBox.Items.Add((New-AppEntry $Path))
    Save-Actions
}

function Show-ProcessPicker {
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = T 'picker.title'; $dlg.Font = $form.Font; $dlg.StartPosition = 'CenterParent'
    $dlg.ClientSize = New-Object System.Drawing.Size([int](620 * $scale), [int](480 * $scale))
    $dlg.MinimizeBox = $false; $dlg.ShowInTaskbar = $false

    $lv = New-Object System.Windows.Forms.ListView
    $lv.Dock = 'Fill'; $lv.View = 'Details'; $lv.FullRowSelect = $true; $lv.MultiSelect = $false; $lv.Sorting = 'Ascending'
    [void]$lv.Columns.Add((T 'picker.colProgram'), [int](250 * $scale))
    [void]$lv.Columns.Add((T 'picker.colFile'), [int](340 * $scale))
    $dlg.Cursor = 'WaitCursor'
    $procs = Get-Process | Where-Object { $_.Id -ne $PID -and $_.Path -and $_.Path -notlike "$env:WINDIR\*" } |
        Group-Object Path | ForEach-Object { $_.Group[0] }
    foreach ($p in $procs) {
        $name = if ($p.Description) { $p.Description } else { $p.ProcessName }
        $item = New-Object System.Windows.Forms.ListViewItem($name)
        [void]$item.SubItems.Add($p.Path)
        $item.Tag = $p.Path
        [void]$lv.Items.Add($item)
    }
    $dlg.Cursor = 'Default'
    $lv.Add_DoubleClick({ $this.FindForm().DialogResult = 'OK' })

    $buttons = New-Object System.Windows.Forms.FlowLayoutPanel
    $buttons.Dock = 'Bottom'; $buttons.AutoSize = $true; $buttons.FlowDirection = 'RightToLeft'; $buttons.Padding = '6'
    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = T 'btn.cancel'; $btnCancel.AutoSize = $true; $btnCancel.DialogResult = 'Cancel'
    $btnOk = New-Object System.Windows.Forms.Button
    $btnOk.Text = T 'btn.addConfirm'; $btnOk.AutoSize = $true; $btnOk.DialogResult = 'OK'
    $buttons.Controls.AddRange(@($btnCancel, $btnOk))
    $dlg.AcceptButton = $btnOk; $dlg.CancelButton = $btnCancel
    $dlg.Controls.AddRange(@($lv, $buttons))

    $result = $null
    if ($dlg.ShowDialog($form) -eq 'OK' -and $lv.SelectedItems.Count) { $result = $lv.SelectedItems[0].Tag }
    $dlg.Dispose()
    $result
}

foreach ($c in $chkHdr, $chkHdrRevert, $chkAudio, $chkAudioRevert, $chkClose, $chkCloseRevert, $chkStart, $chkStartRevert) {
    $c.Add_CheckedChanged({ Save-Actions })
}
$cmbAudio.Add_SelectedIndexChanged({ Save-Actions })
$cmbAudio.Add_DropDown({ Update-AudioDeviceList })   # reload when opened (e.g. headset plugged in)

$btnCloseAdd.Add_Click({ $script:addTarget = $lstClose; $addMenu.Show($btnCloseAdd, 0, $btnCloseAdd.Height) })
$btnStartAdd.Add_Click({ $script:addTarget = $lstStart; $addMenu.Show($btnStartAdd, 0, $btnStartAdd.Height) })
$btnCloseRemove.Add_Click({ if ($lstClose.SelectedItem) { $lstClose.Items.Remove($lstClose.SelectedItem); Save-Actions } })
$btnStartRemove.Add_Click({ if ($lstStart.SelectedItem) { $lstStart.Items.Remove($lstStart.SelectedItem); Save-Actions } })
$miAddRunning.Add_Click({ Add-AppToList $script:addTarget (Show-ProcessPicker) })
$miAddFile.Add_Click({
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Title = T 'dlg.chooseProgram'; $dlg.Filter = T 'dlg.exeFilter'
    if ($dlg.ShowDialog($form) -eq 'OK') { Add-AppToList $script:addTarget $dlg.FileName }
})

#endregion

function Get-SelectedGame {
    if ($list.SelectedItems.Count -eq 0) { return $null }
    $list.SelectedItems[0].Tag
}

function Update-RemoveButton {
    $game = Get-SelectedGame
    $btnRemove.Enabled = $game -and ($removableSources -contains $game.Source)
}

function Show-Games {
    $script:loading = $true
    $list.BeginUpdate()
    $list.Items.Clear()
    $filter = $txtSearch.Text.Trim()
    foreach ($game in $games) {
        $isSelected = $selected.ContainsKey($game.Key)
        if ($chkOnlySelected.Checked -and -not $isSelected) { continue }
        if ($filter -and $game.Name -notlike "*$filter*" -and ($game.Exes -join ' ') -notlike "*$filter*") { continue }
        $item = New-Object System.Windows.Forms.ListViewItem($game.Name)
        [void]$item.SubItems.Add((Get-SourceText $game.Source))
        [void]$item.SubItems.Add(($game.Exes -join ', '))
        $item.Checked = $isSelected
        $item.Tag = $game
        $item.ToolTipText = $game.Path
        if ($game.Source -eq 'Missing') { $item.ForeColor = $colorOff }
        [void]$list.Items.Add($item)
    }
    $list.EndUpdate()
    $script:loading = $false
    Update-RemoveButton
    Update-Status
}

function Invoke-Scan {
    $form.Cursor = 'WaitCursor'
    $status.Text = T 'status.scanning'
    [System.Windows.Forms.Application]::DoEvents()
    Find-AllGames
    Save-All
    $form.Cursor = 'Default'
    Show-Games
}

function Remove-SelectedGame {
    $game = Get-SelectedGame
    if (-not $game) { return }
    switch ($game.Source) {
        'Custom' {
            $customExes.Remove($game.Origin)
            $selected.Remove($game.Key)
            $script:games = @($games | Where-Object { $_.Key -ne $game.Key })
        }
        'Folder' {
            $answer = [System.Windows.Forms.MessageBox]::Show($form, (T 'dlg.removeFolder' $game.Origin), (T 'btn.remove'), 'YesNo')
            if ($answer -ne 'Yes') { return }
            $customFolders.Remove($game.Origin)
            foreach ($g in @($games | Where-Object { $_.Origin -eq $game.Origin })) { $selected.Remove($g.Key) }
            $script:games = @($games | Where-Object { $_.Origin -ne $game.Origin })
        }
        'Missing' {
            $selected.Remove($game.Key)
            $script:games = @($games | Where-Object { $_.Key -ne $game.Key })
        }
        default { return }
    }
    Save-All
    Show-Games
}

$list.Add_ItemChecked({
    param($sender, $e)
    if ($script:loading) { return }
    $game = $e.Item.Tag
    if ($e.Item.Checked) {
        $selected[$game.Key] = [pscustomobject]@{ Key = $game.Key; Name = $game.Name; Exes = @($game.Exes) }
    } else {
        $selected.Remove($game.Key)
    }
    Save-All
    Update-Status
    Update-Running
})
$list.Add_SelectedIndexChanged({ Update-RemoveButton })

$menu.Add_Opening({
    param($sender, $e)
    $game = Get-SelectedGame
    if (-not $game) { $e.Cancel = $true; return }
    $miOpen.Enabled = [bool]$game.Path -and (Test-Path $game.Path)
    $miRemove.Enabled = $removableSources -contains $game.Source
})
$miOpen.Add_Click({
    $game = Get-SelectedGame
    if (Test-Path $game.Path -PathType Leaf) { Start-Process explorer.exe -ArgumentList "/select,`"$($game.Path)`"" }
    else { Start-Process explorer.exe -ArgumentList "`"$($game.Path)`"" }
})
$miRemove.Add_Click({ Remove-SelectedGame })

$txtSearch.Add_TextChanged({ Show-Games })
$chkOnlySelected.Add_CheckedChanged({ Show-Games })
$btnRescan.Add_Click({ Invoke-Scan })
$btnRemove.Add_Click({ Remove-SelectedGame })

$btnToggleHdr.Add_Click({
    $enable = -not ($script:hdrDisplays | Where-Object HdrEnabled)
    try {
        foreach ($d in $script:hdrDisplays) { if ($d.HdrEnabled -ne $enable) { Set-Hdr $d $enable } }
    } catch {
        [void][System.Windows.Forms.MessageBox]::Show($form, (T 'dlg.hdrError' $_.Exception.Message), 'HDR')
    }
    Start-Sleep -Milliseconds 700
    Update-Hdr
})

$btnAddExe.Add_Click({
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Title = T 'dlg.chooseGame'; $dlg.Filter = T 'dlg.exeFilter'
    if ($dlg.ShowDialog($form) -ne 'OK') { return }
    if (-not ($customExes -contains $dlg.FileName)) { [void]$customExes.Add($dlg.FileName) }
    $game = New-ExeGame $dlg.FileName
    $selected[$game.Key] = [pscustomobject]@{ Key = $game.Key; Name = $game.Name; Exes = @($game.Exes) }
    $script:games = @(@($games | Where-Object { $_.Key -ne $game.Key }) + $game | Sort-Object Name)
    Save-All
    Show-Games
})

$btnAddFolder.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = T 'dlg.folderDescription'
    if ($dlg.ShowDialog($form) -ne 'OK') { return }
    $folder = $dlg.SelectedPath
    $new = @(Get-FolderGames $folder 'Folder')
    if ($new.Count -eq 0) {
        [void][System.Windows.Forms.MessageBox]::Show($form, (T 'dlg.noGamesInFolder'), (T 'btn.addFolder'))
        return
    }
    if (-not ($customFolders -contains $folder)) { [void]$customFolders.Add($folder) }
    $keys = $new | ForEach-Object Key
    $script:games = @(@($games | Where-Object { $keys -notcontains $_.Key }) + $new | Sort-Object Name)
    Save-All
    Show-Games
    [void][System.Windows.Forms.MessageBox]::Show($form, (T 'dlg.gamesFound' $new.Count), (T 'btn.addFolder'))
})

$btnLog.Add_Click({
    if (Test-Path $logFile) { Start-Process notepad.exe -ArgumentList "`"$logFile`"" }
    else { [void][System.Windows.Forms.MessageBox]::Show($form, (T 'dlg.noLog'), (T 'btn.log')) }
})

$chkAutostart.Add_CheckedChanged({
    if ($script:loading) { return }
    try {
        if ($chkAutostart.Checked) {
            & (Join-Path $root 'Install.ps1') -NoStart | Out-Null
        } else {
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
        }
    } catch {
        [void][System.Windows.Forms.MessageBox]::Show($form, (T 'dlg.autostartError' $_.Exception.Message), (T 'opt.autostart'))
    }
    Update-ServiceState
    Update-Status
})

$btnService.Add_Click({
    $form.Cursor = 'WaitCursor'
    $procs = @(Get-WatcherProcesses)
    if ($procs) {
        foreach ($p in $procs) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    } else {
        # conhost --headless: no console window, even with Windows Terminal as the default
        $watcher = Join-Path $root 'Watcher.ps1'
        Start-Process conhost.exe -ArgumentList "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$watcher`""
    }
    Start-Sleep -Milliseconds 1200
    Update-ServiceState
    $form.Cursor = 'Default'
    Update-Status
})

# Switch the language at runtime: re-apply all texts
function Update-Language {
    Set-UiTexts
    Update-Hdr
    Update-Running
    Update-AudioLabel
    Update-AudioDeviceList
    Update-ServiceState
    Show-Games
}

$script:loading = $true
foreach ($l in Get-Languages) {
    $item = New-Object LangItem
    $item.Code = $l.Code; $item.Name = $l.Name
    [void]$cmbLanguage.Items.Add($item)
    if ($l.Code -eq $script:language) { $cmbLanguage.SelectedItem = $item }
}
$script:loading = $false

$cmbLanguage.Add_SelectedIndexChanged({
    if ($script:loading -or -not $cmbLanguage.SelectedItem) { return }
    Initialize-Language $cmbLanguage.SelectedItem.Code
    Save-All
    Update-Language
})

# Refresh the display every 2 s, the service state every 10 s
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 2000
$timer.Add_Tick({
    Update-Hdr
    Update-Running
    Update-AudioLabel
    $script:tick++
    if ($script:tick % 5 -eq 0) { Update-ServiceState; Update-Status }
})

Set-UiTexts
$form.Add_Shown({
    Show-Actions
    Update-AudioLabel
    Update-Hdr
    Update-ServiceState
    Invoke-Scan
    Update-Running
    $timer.Start()
})
$form.Add_FormClosed({ $timer.Stop() })
[void]$form.ShowDialog()

#endregion
