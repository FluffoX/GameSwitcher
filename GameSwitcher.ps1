# GameSwitcher GUI. Started via GameSwitcher.exe (or the GameSwitcher shortcut).
# Finds installed games, lets the user pick games and per-game profiles, and saves everything to
# settings.json, which the watcher picks up immediately.
#   .\GameSwitcher.ps1 -ScanOnly   list the found games without a window
#   .\GameSwitcher.ps1 -Demo       show sample data (for screenshots); nothing is saved
param([switch]$ScanOnly, [switch]$Demo)

$root = $PSScriptRoot
$logFile = Join-Path $root 'watcher.log'
$pausePath = Join-Path $root 'pause.json'
$playtimePath = Join-Path $root 'playtime.json'
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

# $Launch: store URI or program used for "Launch game"; the largest executable is the fallback
function New-Game([string]$Name, [string]$Source, [string]$Path, [string]$Origin, [string]$Launch) {
    $files = @(Find-GameExes $Path)
    if ($files.Count -eq 0) { return }
    $main = $files | Sort-Object { (New-Object IO.FileInfo $_).Length } -Descending | Select-Object -First 1
    if (-not $Launch -and $Source -eq 'Xbox') {
        $helper = Join-Path $Path 'Content\gamelaunchhelper.exe'
        if (Test-Path $helper) { $Launch = $helper }
    }
    [pscustomobject]@{
        Name = $Name; Source = $Source; Path = $Path; Key = $Path.TrimEnd('\').ToLower(); Origin = $Origin
        Exes = @($files | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object -Unique)
        MainExe = $main; Launch = $Launch
    }
}

function New-ExeGame([string]$Exe, [string]$Source = 'Custom', [string]$Key) {
    $name = $null
    try { $name = [Diagnostics.FileVersionInfo]::GetVersionInfo($Exe).ProductName } catch {}
    if (-not $name -or -not $name.Trim()) { $name = [IO.Path]::GetFileNameWithoutExtension($Exe) }
    if (-not $Key) { $Key = $Exe.ToLower() }
    [pscustomobject]@{
        Name = $name.Trim(); Source = $Source; Path = $Exe; Key = $Key; Origin = $Exe
        Exes = @([IO.Path]::GetFileName($Exe)); MainExe = $Exe; Launch = $Exe
    }
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
            $appId = [regex]::Match($acf.Name, '\d+').Value
            if (-not $dir -or $name -match '^(Steamworks Common Redistributables|Proton|Steam Linux Runtime|SteamVR)') { continue }
            $path = Join-Path $lib "steamapps\common\$dir"
            if (Test-Path $path) { New-Game $name 'Steam' $path 'Steam' "steam://rungameid/$appId" }
        }
    }
}

function Get-EpicGames {
    $dir = Join-Path $env:ProgramData 'Epic\EpicGamesLauncher\Data\Manifests'
    foreach ($f in Get-ChildItem $dir -Filter '*.item' -ErrorAction SilentlyContinue) {
        try { $m = Get-Content $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } catch { continue }
        if ($m.bIsIncompleteInstall) { continue }
        if ($m.AppCategories -and ($m.AppCategories -notcontains 'games')) { continue }
        if (-not ($m.InstallLocation -and (Test-Path $m.InstallLocation))) { continue }
        $uri = if ($m.CatalogNamespace -and $m.CatalogItemId) {
            "com.epicgames.launcher://apps/$($m.CatalogNamespace)%3A$($m.CatalogItemId)%3A$($m.AppName)?action=launch&silent=true"
        } else { "com.epicgames.launcher://apps/$($m.AppName)?action=launch&silent=true" }
        New-Game $m.DisplayName 'Epic' $m.InstallLocation 'Epic' $uri
    }
}

function Get-GogGames {
    foreach ($k in Get-ChildItem 'HKLM:\SOFTWARE\WOW6432Node\GOG.com\Games' -ErrorAction SilentlyContinue) {
        $p = Get-ItemProperty $k.PSPath
        if ($p.path -and (Test-Path $p.path)) { New-Game $p.gameName 'GOG' $p.path 'GOG' $p.exe }
    }
}

function Get-UbisoftGames {
    foreach ($k in Get-ChildItem 'HKLM:\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs' -ErrorAction SilentlyContinue) {
        $p = (Get-ItemProperty $k.PSPath).InstallDir
        if ($p) {
            $p = ($p -replace '/', '\').TrimEnd('\')
            if (Test-Path $p) { New-Game (Split-Path $p -Leaf) 'Ubisoft' $p 'Ubisoft' "uplay://launch/$($k.PSChildName)/0" }
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

# Games Windows has seen running (Game Bar / Game Mode); same keys as the watcher's automatic detection
function Get-WindowsGames {
    foreach ($k in Get-ChildItem 'HKCU:\System\GameConfigStore\Children' -ErrorAction SilentlyContinue) {
        $path = (Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue).MatchedExeFullPath
        if ($path -and (Test-Path $path)) { New-ExeGame $path 'Windows' ('auto:' + $path.ToLower()) }
    }
}

#endregion

#region Settings

. (Join-Path $root 'Actions.ps1')

$selected = @{}                 # Key -> @{Key; Name; Exes}
$profiles = @{}                 # Key -> action set of games with their own profile
$customFolders = New-Object System.Collections.ArrayList
$customExes = New-Object System.Collections.ArrayList
$games = @()
$options = @{ AutoDetect = $false; Hotkey = 'Ctrl+Alt+G'; Notifications = $true; Theme = 'system'; CheckUpdates = $true }

$s = Read-Settings
$actions = Complete-Actions $s.Actions
if ($null -ne $s.TurnOffAfterGame -and -not $s.Actions) { $actions.Hdr.Revert = [bool]$s.TurnOffAfterGame }
foreach ($e in $s.Selected) { $selected[$e.Key] = [pscustomobject]@{ Key = $e.Key; Name = $e.Name; Exes = @($e.Exes) } }
foreach ($p in Get-Members $s.Profiles) { $profiles[$p.Name] = Complete-Actions $p.Value }
foreach ($f in $s.CustomFolders) { [void]$customFolders.Add($f) }
foreach ($e in $s.CustomExes) { [void]$customExes.Add($e) }
foreach ($k in @($options.Keys)) { if ($null -ne $s.$k) { $options[$k] = $s.$k } }
Initialize-Language (Get-ConfiguredLanguage)

# The legacy games.txt (version 1.x) is no longer used
$legacyGames = Join-Path $root 'games.txt'
if (-not $Demo -and (Test-Path $legacyGames)) { Remove-Item $legacyGames -ErrorAction SilentlyContinue }

function Save-All {
    if ($Demo) { return }
    @{
        Selected = @($selected.Values); Profiles = $profiles; Actions = $actions
        CustomFolders = @($customFolders); CustomExes = @($customExes); Language = $script:language
        AutoDetect = $options.AutoDetect; Hotkey = $options.Hotkey; Notifications = $options.Notifications
        Theme = $options.Theme; CheckUpdates = $options.CheckUpdates
        # all found games, so the watcher's automatic detection uses the same keys as this list
        Library = @($games | Where-Object Source -ne 'Missing' | ForEach-Object { @{ Key = $_.Key; Name = $_.Name; Exes = $_.Exes } })
    } | ConvertTo-Json -Depth 8 | Set-Content $settingsPath -Encoding UTF8
}

function Find-AllGames {
    if ($Demo) { $script:games = @(Get-DemoGames); return }
    $all = @(Get-SteamGames) + @(Get-EpicGames) + @(Get-GogGames) + @(Get-UbisoftGames) + @(Get-XboxGames) +
        @(Get-FolderGames (Join-Path $env:ProgramFiles 'EA Games') 'EA')
    foreach ($f in $customFolders) { $all += @(Get-FolderGames $f 'Folder') }
    foreach ($e in $customExes) { if (Test-Path $e) { $all += New-ExeGame $e } }
    $knownExes = @($all | ForEach-Object { $_.Exes }) | ForEach-Object { $_.ToLower() }
    $all += @(Get-WindowsGames | Where-Object { $knownExes -notcontains $_.Exes[0].ToLower() })

    $found = [ordered]@{}
    foreach ($g in $all) { if ($g -and -not $found.Contains($g.Key)) { $found[$g.Key] = $g } }
    foreach ($s in @($selected.Values)) {
        if ($found.Contains($s.Key)) {
            $s.Exes = $found[$s.Key].Exes   # refresh the executable list after game updates
        } else {
            $found[$s.Key] = [pscustomobject]@{ Name = $s.Name; Source = 'Missing'; Path = ''; Key = $s.Key; Exes = @($s.Exes); Origin = $null; MainExe = $null; Launch = $null }
        }
    }
    $script:games = @($found.Values | Sort-Object Name)
}

function Get-Playtime {
    $data = @{}
    if ($Demo) { return $script:demoPlaytime }
    if (Test-Path $playtimePath) {
        try { foreach ($m in Get-Members (Get-Content $playtimePath -Raw -Encoding UTF8 | ConvertFrom-Json)) { $data[$m.Name] = $m.Value } } catch {}
    }
    $data
}

#endregion

#region Demo data (screenshots only)

function Get-DemoGames {
    $list = @(
        @('Arcfall Legends', 'Steam', 'ArcfallLegends.exe'), @('Deep Horizon', 'Epic', 'DeepHorizon-Win64-Shipping.exe'),
        @('Ironclad Frontier', 'Steam', 'Ironclad.exe'), @('Kingdoms of Ash', 'GOG', 'KingdomsOfAsh.exe'),
        @('Mythwood Chronicles', 'Steam', 'Mythwood.exe'), @('Neon Drift', 'Xbox', 'NeonDrift.exe'),
        @('Pixel Brawlers', 'Steam', 'PixelBrawlers.exe'), @('Shadowline', 'Ubisoft', 'Shadowline.exe'),
        @('Starfall Tactics', 'EA', 'StarfallTactics.exe'), @('Velocity Rush', 'Epic', 'VelocityRush.exe')
    )
    foreach ($g in $list) {
        [pscustomobject]@{ Name = $g[0]; Source = $g[1]; Path = ''; Key = 'demo:' + $g[0]; Origin = $null; Exes = @($g[2]); MainExe = $null; Launch = $null }
    }
}

if ($Demo) {
    $selected = @{}
    foreach ($n in 'Arcfall Legends', 'Deep Horizon', 'Mythwood Chronicles', 'Neon Drift', 'Velocity Rush') {
        $selected["demo:$n"] = [pscustomobject]@{ Key = "demo:$n"; Name = $n; Exes = @() }
    }
    $profiles = @{ 'demo:Neon Drift' = (Complete-Actions $null) }
    $actions = Complete-Actions $null
    $actions.Audio.Enabled = $true; $actions.Audio.DeviceId = 'demo-headset'; $actions.Audio.DeviceName = 'Headset (USB Audio)'
    $actions.Power.Enabled = $true
    $actions.KeepAwake.Enabled = $true
    $script:demoPlaytime = @{}
    $now = Get-Date
    foreach ($e in @(@('Arcfall Legends', 152000, 0), @('Deep Horizon', 48200, 2), @('Mythwood Chronicles', 391000, 1), @('Neon Drift', 12600, 5), @('Velocity Rush', 2700, 9))) {
        $script:demoPlaytime["demo:$($e[0])"] = [pscustomobject]@{ Name = $e[0]; Seconds = $e[1]; LastPlayed = $now.AddDays(-$e[2]).ToString('o') }
    }
}

#endregion

if ($ScanOnly) {
    Find-AllGames
    $games | Format-Table Name, Source, Launch, @{ n = 'Processes'; e = { $_.Exes -join ', ' } } -AutoSize -Wrap
    return
}

#region Window setup

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
if (-not ('ListViewSorter' -as [type])) {
    Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @'
using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class AppEntry { public string Path; public string Name; public override string ToString() { return Name; } }
public class Item { public string Code; public string Name; public override string ToString() { return Name; } }

// Sorts by the SubItem's Tag when set (numbers, dates), otherwise by text
public class ListViewSorter : IComparer {
    public int Column;
    public bool Descending;
    public int Compare(object a, object b) {
        ListViewItem.ListViewSubItem x = ((ListViewItem)a).SubItems[Column], y = ((ListViewItem)b).SubItems[Column];
        int r = (x.Tag is IComparable && y.Tag != null && x.Tag.GetType() == y.Tag.GetType())
            ? ((IComparable)x.Tag).CompareTo(y.Tag)
            : string.Compare(x.Text, y.Text, StringComparison.CurrentCultureIgnoreCase);
        return Descending ? -r : r;
    }
}

public static class WinNative {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr h, string app, string idList);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    // Undocumented but widely used (Windows 10 1903+): lets "DarkMode_*" control themes render dark
    [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#133")] static extern bool AllowDarkModeForWindow(IntPtr h, bool allow);
    public static void AllowDarkMode() { try { SetPreferredAppMode(1); } catch { } }
    public static void SetDarkControl(IntPtr h, bool dark, string darkTheme, string lightTheme) {
        try { AllowDarkModeForWindow(h, dark); } catch { }
        SetWindowTheme(h, dark ? darkTheme : lightTheme, null);
        SendMessage(h, 0x031A, IntPtr.Zero, IntPtr.Zero);   // WM_THEMECHANGED
    }
}
'@
}
[void][WinNative]::SetProcessDPIAware()
[System.Windows.Forms.Application]::EnableVisualStyles()

# Only one window: bring an already open one to the front
if (-not $Demo) {
    $guiCreated = $false
    $guiMutex = New-Object System.Threading.Mutex($true, 'Local\GameSwitcherGui', [ref]$guiCreated)
    if (-not $guiCreated) {
        $h = [WinNative]::FindWindow($null, 'GameSwitcher')
        if ($h -ne [IntPtr]::Zero) { [void][WinNative]::ShowWindow($h, 9); [void][WinNative]::SetForegroundWindow($h) }
        return
    }
}

[WinNative]::AllowDarkMode()
$colorOn = [System.Drawing.Color]::FromArgb(0, 150, 70)
$colorOff = [System.Drawing.Color]::Gray
$removableSources = 'Custom', 'Folder', 'Missing'
$iconPath = Join-Path $root 'assets\icon.ico'

$form = New-Object System.Windows.Forms.Form
$form.Text = 'GameSwitcher'
$form.Font = New-Object System.Drawing.Font('Segoe UI', 9)
if (Test-Path $iconPath) { $form.Icon = New-Object System.Drawing.Icon($iconPath) }
$g = $form.CreateGraphics(); $scale = $g.DpiX / 96; $g.Dispose()
$form.ClientSize = New-Object System.Drawing.Size([int](1320 * $scale), [int](760 * $scale))
$form.StartPosition = 'CenterScreen'

function Px([double]$Value) { [int]($Value * $scale) }

function New-Button {
    $b = New-Object System.Windows.Forms.Button
    $b.AutoSize = $true; $b.AutoSizeMode = 'GrowAndShrink'; $b.Padding = '6,0,6,0'
    $b.MinimumSize = New-Object System.Drawing.Size((Px 80), (Px 26))
    $b
}

function New-Label([string]$Margin = '3,7,3,3') {
    $l = New-Object System.Windows.Forms.Label
    $l.AutoSize = $true; $l.Margin = $Margin
    $l
}

function New-Flow([string]$Dock = 'None', [string]$Padding = '0') {
    $p = New-Object System.Windows.Forms.FlowLayoutPanel
    $p.AutoSize = $true; $p.WrapContents = $false; $p.Margin = '0'; $p.Padding = $Padding
    if ($Dock -ne 'None') { $p.Dock = $Dock; $p.WrapContents = $true }
    $p
}

#endregion

#region Main window controls

# Game list
$list = New-Object System.Windows.Forms.ListView
$list.Dock = 'Fill'; $list.View = 'Details'; $list.CheckBoxes = $true; $list.FullRowSelect = $true
$list.HideSelection = $false; $list.MultiSelect = $false; $list.ShowItemToolTips = $true
foreach ($w in 290, 90, 80, 85, 105, 300) { [void]$list.Columns.Add('', (Px $w)) }
$images = New-Object System.Windows.Forms.ImageList
$images.ColorDepth = 'Depth32Bit'; $images.ImageSize = New-Object System.Drawing.Size((Px 16), (Px 16))
$list.SmallImageList = $images
$sorter = New-Object ListViewSorter
$list.ListViewItemSorter = $sorter

# Top: HDR state, running game, audio, pause, update notice
$statusPanel = New-Flow 'Top' '6,8,6,2'
$lblHdr = New-Label '3,4,12,3'
$btnToggleHdr = New-Button
$lblRunning = New-Label '18,8,3,3'
$lblAudio = New-Label '18,8,3,3'
$btnPause = New-Button; $btnPause.Margin = '24,3,3,3'
$lblPause = New-Label '6,8,3,3'
$lnkUpdate = New-Object System.Windows.Forms.LinkLabel
$lnkUpdate.AutoSize = $true; $lnkUpdate.Margin = '18,8,3,3'; $lnkUpdate.Visible = $false
$statusPanel.Controls.AddRange(@($lblHdr, $btnToggleHdr, $lblRunning, $lblAudio, $btnPause, $lblPause, $lnkUpdate))

# Search and source filter
$searchPanel = New-Flow 'Top' '6,2,6,2'
$lblSearch = New-Label
$txtSearch = New-Object System.Windows.Forms.TextBox
$txtSearch.Width = Px 240
$cmbSource = New-Object System.Windows.Forms.ComboBox
$cmbSource.DropDownStyle = 'DropDownList'; $cmbSource.Width = Px 140; $cmbSource.Margin = '12,3,3,3'
$chkOnlySelected = New-Object System.Windows.Forms.CheckBox
$chkOnlySelected.AutoSize = $true; $chkOnlySelected.Margin = '12,5,3,3'
$btnRescan = New-Button
$searchPanel.Controls.AddRange(@($lblSearch, $txtSearch, $cmbSource, $chkOnlySelected, $btnRescan))

# Bottom buttons
$bottom = New-Flow 'Bottom' '6,4,6,6'
$btnAddExe = New-Button
$btnAddFolder = New-Button
$btnRemove = New-Button; $btnRemove.Enabled = $false
$btnLog = New-Button; $btnLog.Margin = '24,3,3,3'
$btnSettings = New-Button
$bottom.Controls.AddRange(@($btnAddExe, $btnAddFolder, $btnRemove, $btnLog, $btnSettings))

$statusBar = New-Object System.Windows.Forms.StatusStrip
$status = New-Object System.Windows.Forms.ToolStripStatusLabel
[void]$statusBar.Items.Add($status)

#endregion

#region Actions panel

$actionsPanel = New-Object System.Windows.Forms.FlowLayoutPanel
$actionsPanel.Dock = 'Right'; $actionsPanel.Width = Px 430
$actionsPanel.FlowDirection = 'TopDown'; $actionsPanel.WrapContents = $false; $actionsPanel.AutoScroll = $true
$actionsPanel.Padding = '10,4,10,4'
$innerWidth = Px 375

function New-Check([int]$Indent = 0, [int]$Top = 2) {
    $c = New-Object System.Windows.Forms.CheckBox
    $c.AutoSize = $true; $c.Margin = "$(3 + $Indent),$Top,3,1"
    $c.Tag = [int]($innerWidth - $Indent)
    $c
}

function New-Combo {
    $c = New-Object System.Windows.Forms.ComboBox
    $c.DropDownStyle = 'DropDownList'; $c.Width = $innerWidth - (Px 24); $c.Margin = '23,3,3,2'
    $c
}

function New-Number([int]$Max = 100) {
    $n = New-Object System.Windows.Forms.NumericUpDown
    $n.Minimum = 0; $n.Maximum = $Max; $n.Width = Px 60; $n.Margin = '3,3,3,1'
    $n
}

# A checkbox followed by a number field and a unit, e.g. "Set volume to [50] %"
function New-CheckNumber([int]$Top = 12) {
    $row = New-Flow
    $c = New-Check 0 $Top; $c.Tag = [int]($innerWidth - (Px 100))
    $n = New-Number; $n.Margin = "3,$($Top - 2),3,1"
    $u = New-Label "0,$($Top + 2),3,1"; $u.Text = '%'
    $row.Controls.AddRange(@($c, $n, $u))
    @{ Row = $row; Check = $c; Number = $n }
}

function New-Section {
    $l = New-Label '3,16,3,4'
    $l.Font = New-Object System.Drawing.Font($form.Font.FontFamily, 9.5, [System.Drawing.FontStyle]::Bold)
    $l
}

function New-AppList {
    $l = New-Object System.Windows.Forms.ListBox
    $l.Width = $innerWidth - (Px 24); $l.Height = Px 58
    $l.Margin = '23,3,3,2'; $l.HorizontalScrollbar = $true
    $l
}

function New-ButtonRow {
    $row = New-Flow; $row.Margin = '20,0,0,0'
    $row.Controls.AddRange(@((New-Button), (New-Button)))
    $row
}

function New-ScriptRow {
    $row = New-Flow; $row.Margin = '20,0,0,0'
    $t = New-Object System.Windows.Forms.TextBox; $t.Width = $innerWidth - (Px 70); $t.Margin = '3,3,3,3'
    $b = New-Button; $b.Text = '…'; $b.MinimumSize = New-Object System.Drawing.Size((Px 30), (Px 24))
    $row.Controls.AddRange(@($t, $b))
    @{ Row = $row; Text = $t; Browse = $b }
}

$lblActions = New-Label '3,6,3,4'
$lblActions.Font = New-Object System.Drawing.Font('Segoe UI', 10, [System.Drawing.FontStyle]::Bold)
$profileRow = New-Flow
$lblProfile = New-Label '3,7,3,3'
$cmbProfile = New-Object System.Windows.Forms.ComboBox
$cmbProfile.DropDownStyle = 'DropDownList'; $cmbProfile.Width = $innerWidth - (Px 80)
$profileRow.Controls.AddRange(@($lblProfile, $cmbProfile))
$chkOwnProfile = New-Check 0 4

$secDisplay = New-Section
$chkHdr = New-Check 0 2;                $chkHdrRevert = New-Check 20
$sdr = New-CheckNumber 8;               $chkSdr = $sdr.Check; $numSdr = $sdr.Number;      $chkSdrRevert = New-Check 20
$chkDisplay = New-Check 0 10;           $cmbDisplay = New-Combo;                          $chkDisplayRevert = New-Check 20
$chkMonitors = New-Check 0 10;          $chkMonitorsRevert = New-Check 20
$chkNight = New-Check 0 10;             $chkNightRevert = New-Check 20

$secAudio = New-Section
$chkAudio = New-Check 0 2;              $cmbAudio = New-Combo;                            $chkAudioRevert = New-Check 20
$chkMic = New-Check 0 10;               $cmbMic = New-Combo;                              $chkMicRevert = New-Check 20
$vol = New-CheckNumber 8;               $chkVolume = $vol.Check; $numVolume = $vol.Number; $chkVolumeRevert = New-Check 20

$secSystem = New-Section
$chkPower = New-Check 0 2;              $cmbPower = New-Combo;                            $chkPowerRevert = New-Check 20
$chkKeepAwake = New-Check 0 10

$secPrograms = New-Section
$chkClose = New-Check 0 2;              $lstClose = New-AppList
$closeRow = New-ButtonRow;              $btnCloseAdd, $btnCloseRemove = $closeRow.Controls[0], $closeRow.Controls[1]
$chkCloseRevert = New-Check 20
$chkStart = New-Check 0 10;             $lstStart = New-AppList
$startRow = New-ButtonRow;              $btnStartAdd, $btnStartRemove = $startRow.Controls[0], $startRow.Controls[1]
$chkStartRevert = New-Check 20
$chkScript = New-Check 0 10
$lblScriptStart = New-Label '23,4,3,0'; $scriptStart = New-ScriptRow
$lblScriptEnd = New-Label '23,4,3,0';   $scriptEnd = New-ScriptRow

$actionsPanel.Controls.AddRange(@(
    $lblActions, $profileRow, $chkOwnProfile,
    $secDisplay, $chkHdr, $chkHdrRevert, $sdr.Row, $chkSdrRevert, $chkDisplay, $cmbDisplay, $chkDisplayRevert,
    $chkMonitors, $chkMonitorsRevert, $chkNight, $chkNightRevert,
    $secAudio, $chkAudio, $cmbAudio, $chkAudioRevert, $chkMic, $cmbMic, $chkMicRevert, $vol.Row, $chkVolumeRevert,
    $secSystem, $chkPower, $cmbPower, $chkPowerRevert, $chkKeepAwake,
    $secPrograms, $chkClose, $lstClose, $closeRow, $chkCloseRevert, $chkStart, $lstStart, $startRow, $chkStartRevert,
    $chkScript, $lblScriptStart, $scriptStart.Row, $lblScriptEnd, $scriptEnd.Row))

# Simple fields: control -> action/field, and which controls depend on which checkbox
$bindings = @(
    @($chkHdr, 'Hdr', 'Enabled'), @($chkHdrRevert, 'Hdr', 'Revert'),
    @($chkSdr, 'Sdr', 'Enabled'), @($numSdr, 'Sdr', 'Level'), @($chkSdrRevert, 'Sdr', 'Revert'),
    @($chkDisplay, 'Display', 'Enabled'), @($chkDisplayRevert, 'Display', 'Revert'),
    @($chkMonitors, 'Monitors', 'Enabled'), @($chkMonitorsRevert, 'Monitors', 'Revert'),
    @($chkNight, 'NightLight', 'Enabled'), @($chkNightRevert, 'NightLight', 'Revert'),
    @($chkAudio, 'Audio', 'Enabled'), @($chkAudioRevert, 'Audio', 'Revert'),
    @($chkMic, 'Mic', 'Enabled'), @($chkMicRevert, 'Mic', 'Revert'),
    @($chkVolume, 'Volume', 'Enabled'), @($numVolume, 'Volume', 'Level'), @($chkVolumeRevert, 'Volume', 'Revert'),
    @($chkPower, 'Power', 'Enabled'), @($chkPowerRevert, 'Power', 'Revert'),
    @($chkKeepAwake, 'KeepAwake', 'Enabled'),
    @($chkClose, 'CloseApps', 'Enabled'), @($chkCloseRevert, 'CloseApps', 'Revert'),
    @($chkStart, 'StartApps', 'Enabled'), @($chkStartRevert, 'StartApps', 'Revert'),
    @($chkScript, 'Script', 'Enabled'), @($scriptStart.Text, 'Script', 'Start'), @($scriptEnd.Text, 'Script', 'End')
)
$dependents = @(
    @($chkHdr, @($chkHdrRevert)), @($chkSdr, @($numSdr, $chkSdrRevert)), @($chkDisplay, @($cmbDisplay, $chkDisplayRevert)),
    @($chkMonitors, @($chkMonitorsRevert)), @($chkNight, @($chkNightRevert)),
    @($chkAudio, @($cmbAudio, $chkAudioRevert)), @($chkMic, @($cmbMic, $chkMicRevert)), @($chkVolume, @($numVolume, $chkVolumeRevert)),
    @($chkPower, @($cmbPower, $chkPowerRevert)),
    @($chkClose, @($lstClose, $btnCloseAdd, $btnCloseRemove, $chkCloseRevert)),
    @($chkStart, @($lstStart, $btnStartAdd, $btnStartRemove, $chkStartRevert)),
    @($chkScript, @($lblScriptStart, $scriptStart.Text, $scriptStart.Browse, $lblScriptEnd, $scriptEnd.Text, $scriptEnd.Browse))
)
$nightLightSupported = $Demo -or ($null -ne (Get-NightLight))

#endregion

#region Menus

$menu = New-Object System.Windows.Forms.ContextMenuStrip
$miLaunch = $menu.Items.Add('')
$miLaunch.Font = New-Object System.Drawing.Font($miLaunch.Font, [System.Drawing.FontStyle]::Bold)
$miProfile = $menu.Items.Add('')
$miOpen = $menu.Items.Add('')
$miRemove = $menu.Items.Add('')
$list.ContextMenuStrip = $menu

$addMenu = New-Object System.Windows.Forms.ContextMenuStrip
$miAddRunning = $addMenu.Items.Add('')
$miAddFile = $addMenu.Items.Add('')
$script:addTarget = $null

$pauseMenu = New-Object System.Windows.Forms.ContextMenuStrip
$miPause1 = $pauseMenu.Items.Add('')
$miPause2 = $pauseMenu.Items.Add('')
$miPauseReboot = $pauseMenu.Items.Add('')

# Order matters: add the Fill control first and the outermost bars last
$form.Controls.AddRange(@($list, $actionsPanel, $searchPanel, $statusPanel, $bottom, $statusBar))

#endregion

#region Texts and theme

$script:loading = $false
$script:serviceRunning = $false
$script:hdrDisplays = @()
$script:tick = 0
$script:editKey = ''

function Set-UiTexts {
    # Languages with their own script (e.g. CJK) can name a matching Windows font via "_font"
    $fontName = $script:strings['_font']
    if (-not $fontName) { $fontName = 'Segoe UI' }
    $form.Font = New-Object System.Drawing.Font($fontName, 9)
    $lblHdr.Font = New-Object System.Drawing.Font($fontName, 11, [System.Drawing.FontStyle]::Bold)
    $lblActions.Font = New-Object System.Drawing.Font($fontName, 10, [System.Drawing.FontStyle]::Bold)
    foreach ($sec in $secDisplay, $secAudio, $secSystem, $secPrograms) { $sec.Font = New-Object System.Drawing.Font($fontName, 9.5, [System.Drawing.FontStyle]::Bold) }

    $i = 0
    foreach ($k in 'col.game', 'col.source', 'col.profile', 'col.playtime', 'col.lastPlayed', 'col.processes') { $list.Columns[$i++].Text = T $k }
    $lblSearch.Text = T 'search.label'
    $chkOnlySelected.Text = T 'search.onlySelected'
    $btnRescan.Text = T 'btn.rescan'
    $btnAddExe.Text = T 'btn.addExe'
    $btnAddFolder.Text = T 'btn.addFolder'
    $btnRemove.Text = T 'btn.remove'
    $btnLog.Text = T 'btn.log'
    $btnSettings.Text = T 'btn.settings'
    $btnPause.Text = T 'btn.pause'
    $lblActions.Text = T 'actions.title'
    $lblProfile.Text = T 'profile.label'
    $chkOwnProfile.Text = T 'profile.own'
    $secDisplay.Text = T 'sec.display'; $secAudio.Text = T 'sec.audio'; $secSystem.Text = T 'sec.system'; $secPrograms.Text = T 'sec.programs'
    $chkHdr.Text = T 'act.hdr';               $chkHdrRevert.Text = T 'act.hdrRevert'
    $chkSdr.Text = T 'act.sdr';               $chkSdrRevert.Text = T 'act.sdrRevert'
    $chkDisplay.Text = T 'act.display';       $chkDisplayRevert.Text = T 'act.displayRevert'
    $chkMonitors.Text = T 'act.monitors';     $chkMonitorsRevert.Text = T 'act.monitorsRevert'
    $chkNight.Text = (T 'act.nightLight') + $(if (-not $nightLightSupported) { ' – ' + (T 'label.unsupported') })
    $chkNightRevert.Text = T 'act.nightLightRevert'
    $chkAudio.Text = T 'act.audio';           $chkAudioRevert.Text = T 'act.audioRevert'
    $chkMic.Text = T 'act.mic';               $chkMicRevert.Text = T 'act.micRevert'
    $chkVolume.Text = T 'act.volume';         $chkVolumeRevert.Text = T 'act.volumeRevert'
    $chkPower.Text = T 'act.power';           $chkPowerRevert.Text = T 'act.powerRevert'
    $chkKeepAwake.Text = T 'act.keepAwake'
    $chkClose.Text = T 'act.close';           $chkCloseRevert.Text = T 'act.closeRevert'
    $chkStart.Text = T 'act.start';           $chkStartRevert.Text = T 'act.startRevert'
    $chkScript.Text = T 'act.script'
    $lblScriptStart.Text = T 'act.scriptStart'; $lblScriptEnd.Text = T 'act.scriptEnd'
    foreach ($b in $btnCloseAdd, $btnStartAdd) { $b.Text = T 'btn.add' }
    foreach ($b in $btnCloseRemove, $btnStartRemove) { $b.Text = T 'btn.remove' }
    $miLaunch.Text = T 'menu.launch'
    $miProfile.Text = T 'menu.editProfile'
    $miOpen.Text = T 'menu.openLocation'
    $miRemove.Text = T 'menu.removeFromList'
    $miAddRunning.Text = T 'menu.addRunning'
    $miAddFile.Text = T 'menu.addFile'
    $miPause1.Text = T 'pause.1h'; $miPause2.Text = T 'pause.2h'; $miPauseReboot.Text = T 'pause.reboot'
}

function Test-DarkMode {
    switch ($options.Theme) {
        'dark' { $true }
        'light' { $false }
        default { (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -ErrorAction SilentlyContinue).AppsUseLightTheme -eq 0 }
    }
}

# Colors all controls of a window for the light or dark design
function Set-Theme([System.Windows.Forms.Form]$Window = $form) {
    $dark = Test-DarkMode
    $back = if ($dark) { [System.Drawing.Color]::FromArgb(32, 32, 32) } else { [System.Drawing.SystemColors]::Control }
    $inputBack = if ($dark) { [System.Drawing.Color]::FromArgb(45, 45, 45) } else { [System.Drawing.SystemColors]::Window }
    $text = if ($dark) { [System.Drawing.Color]::FromArgb(235, 235, 235) } else { [System.Drawing.SystemColors]::ControlText }
    $button = [System.Drawing.Color]::FromArgb(55, 55, 55)

    $apply = {
        param($parent)
        foreach ($c in $parent.Controls) {
            if ($c -is [System.Windows.Forms.Button]) {
                if ($dark) { $c.FlatStyle = 'Flat'; $c.BackColor = $button; $c.ForeColor = $text; $c.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(90, 90, 90) }
                else { $c.FlatStyle = 'Standard'; $c.BackColor = [System.Drawing.SystemColors]::Control; $c.UseVisualStyleBackColor = $true; $c.ForeColor = $text }
            } elseif ($c -is [System.Windows.Forms.TextBox] -or $c -is [System.Windows.Forms.ListBox] -or $c -is [System.Windows.Forms.ListView] -or $c -is [System.Windows.Forms.NumericUpDown]) {
                $c.BackColor = $inputBack; $c.ForeColor = $text
            } elseif ($c -is [System.Windows.Forms.ComboBox]) {
                $c.FlatStyle = if ($dark) { 'Flat' } else { 'Standard' }
                $c.BackColor = $inputBack; $c.ForeColor = $text
            } elseif ($c -is [System.Windows.Forms.LinkLabel]) {
                $c.LinkColor = if ($dark) { [System.Drawing.Color]::FromArgb(110, 170, 255) } else { [System.Drawing.Color]::FromArgb(0, 102, 204) }
            } elseif ($c -ne $lblHdr) {
                $c.BackColor = $back; $c.ForeColor = $text
            }
            if ($c.Controls.Count) { & $apply $c }
        }
    }
    $Window.BackColor = $back; $Window.ForeColor = $text
    & $apply $Window
    foreach ($m in $menu, $addMenu, $pauseMenu) {
        $m.BackColor = $inputBack
        foreach ($item in $m.Items) { $item.ForeColor = $text; $item.BackColor = $inputBack }
    }

    # Title bar, list scrollbars and column headers
    $v = [int]$dark
    [void][WinNative]::DwmSetWindowAttribute($Window.Handle, 20, [ref]$v, 4)
    foreach ($lv in @($Window.Controls | Where-Object { $_ -is [System.Windows.Forms.ListView] }) + @($(if ($Window -eq $form) { $list }))) {
        if (-not $lv) { continue }
        [WinNative]::SetDarkControl($lv.Handle, $dark, 'DarkMode_Explorer', 'Explorer')
        $header = [WinNative]::SendMessage($lv.Handle, 0x101F, [IntPtr]::Zero, [IntPtr]::Zero)   # LVM_GETHEADER
        [WinNative]::SetDarkControl($header, $dark, 'DarkMode_ItemsView', 'ItemsView')
    }
    Update-CheckSizes $Window
}

# Checkboxes with a width limit (Tag) wrap long texts onto several lines instead of cutting them off
function Update-CheckSizes($Parent) {
    foreach ($c in $Parent.Controls) {
        if ($c -is [System.Windows.Forms.CheckBox] -and $c.Tag -is [int]) {
            $max = [int]$c.Tag
            $single = [System.Windows.Forms.TextRenderer]::MeasureText($c.Text, $c.Font)
            if ($single.Width + (Px 22) -le $max) { $c.AutoSize = $true }
            else {
                $flags = [System.Windows.Forms.TextFormatFlags]::WordBreak
                $wrapped = [System.Windows.Forms.TextRenderer]::MeasureText($c.Text, $c.Font, (New-Object System.Drawing.Size(($max - (Px 22)), 0)), $flags)
                $c.AutoSize = $false
                $c.Size = New-Object System.Drawing.Size($max, ($wrapped.Height + (Px 6)))
            }
        }
        if ($c.Controls.Count) { Update-CheckSizes $c }
    }
}

#endregion

#region Status displays

function Get-WatcherProcesses {
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='GameSwitcher.exe'" |
        Where-Object { $_.CommandLine -like '*Watcher.ps1*' -or $_.CommandLine -like '*--watcher*' }
}

function Update-ServiceState { $script:serviceRunning = [bool](Get-WatcherProcesses) }

function Update-Status {
    $service = if ($script:serviceRunning) { T 'service.running' } else { T 'service.stopped' }
    $status.Text = T 'status.summary' $selected.Count $games.Count $service
}

function Update-Hdr {
    if ($Demo) { $script:hdrDisplays = @(); $lblHdr.Text = '● ' + (T 'hdr.off') + '  –  Monitor'; $lblHdr.ForeColor = $colorOff; $btnToggleHdr.Text = T 'btn.hdrOn'; return }
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
    $name = if ($Demo) { 'Headset (USB Audio)' } else { try { [AudioNative]::GetDefaultOutputName() } catch { $null } }
    $lblAudio.Text = T 'audio.current' $(if ($name) { $name } else { '–' })
}

function Get-Pause {
    if (-not (Test-Path $pausePath)) { return $null }
    try { $p = Get-Content $pausePath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $null }
    if ($p.UntilReboot -and $p.BootTime -eq $script:bootTime) { return $p }
    if ($p.Until -and [datetime]$p.Until -gt (Get-Date)) { return $p }
    $null
}

function Update-PauseLabel {
    $p = Get-Pause
    $lblPause.Text = if (-not $p) { '' } elseif ($p.UntilReboot) { T 'pause.untilReboot' } else { T 'pause.until' ([datetime]$p.Until).ToString('t') }
    $btnPause.Text = if ($p) { T 'btn.resume' } else { T 'btn.pause' }
}

function Set-Pause([int]$Hours, [switch]$UntilReboot) {
    $p = if ($UntilReboot) { @{ UntilReboot = $true; BootTime = $script:bootTime } } else { @{ Until = (Get-Date).AddHours($Hours).ToString('o') } }
    $p | ConvertTo-Json | Set-Content $pausePath -Encoding UTF8
    Update-PauseLabel
}

#endregion

#region Game list

function Get-SourceText([string]$Source) {
    switch ($Source) {
        'Custom' { T 'source.custom' }
        'Folder' { T 'source.folder' }
        'Missing' { T 'source.missing' }
        default { $Source }
    }
}

function Format-Duration([double]$Seconds) {
    if ($Seconds -lt 60) { return '' }
    $h = [math]::Floor($Seconds / 3600); $m = [math]::Floor(($Seconds % 3600) / 60)
    if ($h -gt 0) { T 'time.hm' $h $m } else { T 'time.m' $m }
}

function Get-GameIndex([object]$Game) {
    if (-not $Game.MainExe -or -not (Test-Path $Game.MainExe)) { return -1 }
    if (-not $images.Images.ContainsKey($Game.Key)) {
        try { $images.Images.Add($Game.Key, [System.Drawing.Icon]::ExtractAssociatedIcon($Game.MainExe)) } catch { return -1 }
    }
    $images.Images.IndexOfKey($Game.Key)
}

function Get-SelectedGame {
    if ($list.SelectedItems.Count -eq 0) { return $null }
    $list.SelectedItems[0].Tag
}

function Update-RemoveButton {
    $game = Get-SelectedGame
    $btnRemove.Enabled = $game -and ($removableSources -contains $game.Source)
}

function Set-PlaytimeCells([System.Windows.Forms.ListViewItem]$Item, $Playtime) {
    $p = $Playtime[$Item.Tag.Key]
    $secs = if ($p) { [long]$p.Seconds } else { 0L }
    $Item.SubItems[3].Text = Format-Duration $secs
    $Item.SubItems[3].Tag = $secs
    $last = if ($p -and $p.LastPlayed) { [datetime]$p.LastPlayed } else { $null }
    $Item.SubItems[4].Text = if ($last) { $last.ToString('d') } else { '' }
    $Item.SubItems[4].Tag = if ($last) { $last.Ticks } else { 0L }
}

function Update-SourceFilter {
    $current = if ($cmbSource.SelectedItem) { $cmbSource.SelectedItem.Code } else { '' }
    $script:loading = $true
    $cmbSource.Items.Clear()
    $all = New-Object Item; $all.Code = ''; $all.Name = T 'filter.allSources'
    [void]$cmbSource.Items.Add($all)
    $cmbSource.SelectedItem = $all
    foreach ($src in $games | ForEach-Object Source | Sort-Object -Unique) {
        $i = New-Object Item; $i.Code = $src; $i.Name = Get-SourceText $src
        [void]$cmbSource.Items.Add($i)
        if ($src -eq $current) { $cmbSource.SelectedItem = $i }
    }
    $script:loading = $false
}

function Show-Games {
    $script:loading = $true
    $playtime = Get-Playtime
    $keep = (Get-SelectedGame).Key
    $list.BeginUpdate()
    $list.Items.Clear()
    $filter = $txtSearch.Text.Trim()
    $source = if ($cmbSource.SelectedItem) { $cmbSource.SelectedItem.Code } else { '' }
    foreach ($game in $games) {
        $isSelected = $selected.ContainsKey($game.Key)
        if ($chkOnlySelected.Checked -and -not $isSelected) { continue }
        if ($source -and $game.Source -ne $source) { continue }
        if ($filter -and $game.Name -notlike "*$filter*" -and ($game.Exes -join ' ') -notlike "*$filter*") { continue }
        $item = New-Object System.Windows.Forms.ListViewItem($game.Name)
        $item.ImageIndex = Get-GameIndex $game
        [void]$item.SubItems.Add((Get-SourceText $game.Source))
        [void]$item.SubItems.Add($(if (-not $isSelected) { '' } elseif ($profiles.ContainsKey($game.Key)) { T 'profile.ownShort' } else { T 'profile.defaultShort' }))
        [void]$item.SubItems.Add(''); [void]$item.SubItems.Add('')
        [void]$item.SubItems.Add(($game.Exes -join ', '))
        $item.Checked = $isSelected
        $item.Tag = $game
        $item.ToolTipText = $game.Path
        if ($game.Source -eq 'Missing') { $item.ForeColor = $colorOff }
        Set-PlaytimeCells $item $playtime
        [void]$list.Items.Add($item)
        if ($game.Key -eq $keep) { $item.Selected = $true }
    }
    $list.Sort()
    $list.EndUpdate()
    $script:loading = $false
    Update-RemoveButton
    Update-Status
}

function Update-PlaytimeColumns {
    $playtime = Get-Playtime
    foreach ($item in $list.Items) { Set-PlaytimeCells $item $playtime }
}

function Invoke-Scan {
    $form.Cursor = 'WaitCursor'
    $status.Text = T 'status.scanning'
    [System.Windows.Forms.Application]::DoEvents()
    Find-AllGames
    Save-All
    $form.Cursor = 'Default'
    Update-SourceFilter
    Update-ProfileList
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
    Update-ProfileList
    Show-Games
}

function Start-Game([object]$Game) {
    $target = if ($Game.Launch) { $Game.Launch } else { $Game.MainExe }
    if (-not $target) { return }
    try {
        if ($target -match '^[a-z][a-z0-9+.-]*://') { Start-Process $target }
        else { Start-Process -FilePath $target -WorkingDirectory (Split-Path $target) }
    } catch {
        [void][System.Windows.Forms.MessageBox]::Show($form, $_.Exception.Message, 'GameSwitcher')
    }
}

#endregion

#region Profiles and actions

function Copy-Actions($Source) { Complete-Actions ($Source | ConvertTo-Json -Depth 6 | ConvertFrom-Json) }

# The action set shown in the panel and whether it can be edited
function Get-EditActions {
    if (-not $script:editKey) { return @{ Actions = $actions; Editable = $true } }
    if ($profiles.ContainsKey($script:editKey)) { return @{ Actions = $profiles[$script:editKey]; Editable = $true } }
    @{ Actions = $actions; Editable = $false }   # game without own profile: shows the default, read-only
}

function Update-ProfileList {
    $script:loading = $true
    $cmbProfile.Items.Clear()
    $default = New-Object Item; $default.Code = ''; $default.Name = T 'profile.default'
    [void]$cmbProfile.Items.Add($default)
    $current = $default
    foreach ($s in $selected.Values | Sort-Object Name) {
        $i = New-Object Item; $i.Code = $s.Key; $i.Name = $s.Name
        [void]$cmbProfile.Items.Add($i)
        if ($s.Key -eq $script:editKey) { $current = $i }
    }
    $cmbProfile.SelectedItem = $current
    $script:editKey = $current.Code
    $script:loading = $false
    Show-Actions
}

function Select-Profile([string]$Key) {
    foreach ($i in $cmbProfile.Items) { if ($i.Code -eq $Key) { $cmbProfile.SelectedItem = $i; return } }
    $cmbProfile.SelectedIndex = 0
}

function New-AppEntry([string]$Path) {
    $entry = New-Object AppEntry
    $entry.Path = $Path
    $desc = $null
    try { $desc = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileDescription } catch {}
    $file = [IO.Path]::GetFileName($Path)
    $entry.Name = if ($desc -and $desc.Trim()) { "$($desc.Trim())  ($file)" } else { $file }
    $entry
}

function Get-DemoDevices([int]$Flow) {
    $names = if ($Flow -eq 0) { 'Headset (USB Audio)', 'Monitor (HDMI Audio)', 'Speakers (Realtek Audio)' } else { 'Headset Microphone (USB Audio)', 'Microphone (Webcam)' }
    foreach ($n in $names) { $d = New-Object AudioDevice; $d.Id = $(if ($n -like 'Headset (*') { 'demo-headset' } else { $n }); $d.Name = $n; $d }
}

# Fills a device combo box and selects the stored device (shown as "not connected" if missing)
function Update-DeviceCombo([System.Windows.Forms.ComboBox]$Combo, [int]$Flow, $Action) {
    $Combo.Items.Clear()
    $devices = @()
    if ($Demo) { $devices = @(Get-DemoDevices $Flow) } else { try { $devices = @([AudioNative]::GetDevices($Flow)) } catch {} }
    foreach ($d in $devices) { [void]$Combo.Items.Add($d) }
    if ($Action.DeviceId) {
        $match = $devices | Where-Object Id -eq $Action.DeviceId | Select-Object -First 1
        if (-not $match) {
            $match = New-Object AudioDevice
            $match.Id = $Action.DeviceId; $match.Disconnected = $true
            $match.Name = T 'audio.disconnected' $Action.DeviceName
            [void]$Combo.Items.Add($match)
        }
        $Combo.SelectedItem = $match
    }
}

function Update-ModeCombo($Action) {
    $cmbDisplay.Items.Clear()
    $modes = @()
    try { $modes = @([SystemNative]::GetModes()) } catch {}
    foreach ($m in $modes) { [void]$cmbDisplay.Items.Add($m) }
    if ($Action.Width) {
        $match = $modes | Where-Object { $_.Width -eq $Action.Width -and $_.Height -eq $Action.Height -and $_.Hz -eq $Action.Hz } | Select-Object -First 1
        if (-not $match) {
            $match = New-Object DisplayMode; $match.Width = $Action.Width; $match.Height = $Action.Height; $match.Hz = $Action.Hz
            [void]$cmbDisplay.Items.Add($match)
        }
        $cmbDisplay.SelectedItem = $match
    }
}

function Update-PowerCombo($Action) {
    $cmbPower.Items.Clear()
    $current = $null
    foreach ($p in @(Get-PowerPlans)) {
        $i = New-Object Item; $i.Code = $p.Id; $i.Name = $p.Name
        [void]$cmbPower.Items.Add($i)
        if ($p.Id -eq $Action.PlanId) { $current = $i }
    }
    if ($current) { $cmbPower.SelectedItem = $current }
    elseif ($Demo -and $cmbPower.Items.Count) { $cmbPower.SelectedIndex = [math]::Min(1, $cmbPower.Items.Count - 1) }
}

function Update-ActionControls {
    $edit = Get-EditActions
    $chkOwnProfile.Visible = [bool]$script:editKey
    foreach ($b in $bindings) { $b[0].Enabled = $edit.Editable }
    foreach ($d in $dependents) {
        # selection fields stay usable (disabled ones render poorly in the dark design)
        foreach ($c in $d[1]) {
            $isField = $c -is [System.Windows.Forms.ComboBox] -or $c -is [System.Windows.Forms.NumericUpDown]
            $c.Enabled = $edit.Editable -and ($isField -or $d[0].Checked)
        }
    }
    if (-not $nightLightSupported) { $chkNight.Enabled = $false; $chkNightRevert.Enabled = $false }
}

function Show-Actions {
    $edit = Get-EditActions
    $a = $edit.Actions
    $script:loading = $true
    $chkOwnProfile.Checked = $script:editKey -and $profiles.ContainsKey($script:editKey)
    foreach ($b in $bindings) {
        $value = $a[$b[1]][$b[2]]
        switch ($b[0].GetType().Name) {
            'CheckBox' { $b[0].Checked = [bool]$value }
            'NumericUpDown' { $b[0].Value = [math]::Max(0, [math]::Min(100, [int]$value)) }
            'TextBox' { $b[0].Text = [string]$value }
        }
    }
    Update-DeviceCombo $cmbAudio 0 $a.Audio
    Update-DeviceCombo $cmbMic 1 $a.Mic
    Update-ModeCombo $a.Display
    Update-PowerCombo $a.Power
    $lstClose.Items.Clear(); foreach ($p in $a.CloseApps.Apps) { [void]$lstClose.Items.Add((New-AppEntry $p)) }
    $lstStart.Items.Clear(); foreach ($p in $a.StartApps.Apps) { [void]$lstStart.Items.Add((New-AppEntry $p)) }
    $script:loading = $false
    Update-ActionControls
}

function Save-Actions {
    if ($script:loading) { return }
    $edit = Get-EditActions
    if (-not $edit.Editable) { return }
    $a = $edit.Actions
    foreach ($b in $bindings) {
        $a[$b[1]][$b[2]] = switch ($b[0].GetType().Name) {
            'CheckBox' { $b[0].Checked }
            'NumericUpDown' { [int]$b[0].Value }
            'TextBox' { $b[0].Text }
        }
    }
    foreach ($pair in @(@($cmbAudio, 'Audio'), @($cmbMic, 'Mic'))) {
        $dev = $pair[0].SelectedItem
        if ($dev) {
            $a[$pair[1]].DeviceId = $dev.Id
            if (-not $dev.Disconnected) { $a[$pair[1]].DeviceName = $dev.Name }
        }
    }
    if ($cmbDisplay.SelectedItem) {
        $a.Display.Width = $cmbDisplay.SelectedItem.Width; $a.Display.Height = $cmbDisplay.SelectedItem.Height; $a.Display.Hz = $cmbDisplay.SelectedItem.Hz
    }
    if ($cmbPower.SelectedItem) { $a.Power.PlanId = $cmbPower.SelectedItem.Code; $a.Power.PlanName = $cmbPower.SelectedItem.Name }
    $a.CloseApps.Apps = @($lstClose.Items | ForEach-Object { $_.Path })
    $a.StartApps.Apps = @($lstStart.Items | ForEach-Object { $_.Path })
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
    $dlg.Text = T 'picker.title'; $dlg.Font = $form.Font; $dlg.StartPosition = 'CenterParent'; $dlg.Icon = $form.Icon
    $dlg.ClientSize = New-Object System.Drawing.Size((Px 620), (Px 480))
    $dlg.MinimizeBox = $false; $dlg.ShowInTaskbar = $false

    $lv = New-Object System.Windows.Forms.ListView
    $lv.Dock = 'Fill'; $lv.View = 'Details'; $lv.FullRowSelect = $true; $lv.MultiSelect = $false; $lv.Sorting = 'Ascending'
    [void]$lv.Columns.Add((T 'picker.colProgram'), (Px 250))
    [void]$lv.Columns.Add((T 'picker.colFile'), (Px 340))
    $procs = Get-Process | Where-Object { $_.Id -ne $PID -and $_.Path -and $_.Path -notlike "$env:WINDIR\*" } |
        Group-Object Path | ForEach-Object { $_.Group[0] }
    foreach ($p in $procs) {
        $name = if ($p.Description) { $p.Description } else { $p.ProcessName }
        $item = New-Object System.Windows.Forms.ListViewItem($name)
        [void]$item.SubItems.Add($p.Path)
        $item.Tag = $p.Path
        [void]$lv.Items.Add($item)
    }
    $lv.Add_DoubleClick({ $this.FindForm().DialogResult = 'OK' })

    $buttons = New-Object System.Windows.Forms.FlowLayoutPanel
    $buttons.Dock = 'Bottom'; $buttons.AutoSize = $true; $buttons.FlowDirection = 'RightToLeft'; $buttons.Padding = '6'
    $btnCancel = New-Button; $btnCancel.Text = T 'btn.cancel'; $btnCancel.DialogResult = 'Cancel'
    $btnOk = New-Button; $btnOk.Text = T 'btn.addConfirm'; $btnOk.DialogResult = 'OK'
    $buttons.Controls.AddRange(@($btnCancel, $btnOk))
    $dlg.AcceptButton = $btnOk; $dlg.CancelButton = $btnCancel
    $dlg.Controls.AddRange(@($lv, $buttons))
    Set-Theme $dlg

    $result = $null
    if ($dlg.ShowDialog($form) -eq 'OK' -and $lv.SelectedItems.Count) { $result = $lv.SelectedItems[0].Tag }
    $dlg.Dispose()
    $result
}

#endregion

#region Update check

$script:updateJob = $null
$script:updateManual = $false
$script:updateLabel = $null

function Start-UpdateCheck([switch]$Manual) {
    if ($Demo -or $script:updateJob) { return }
    $script:updateManual = [bool]$Manual
    $ps = [PowerShell]::Create().AddScript({
        param($repo)
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        try { (Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -TimeoutSec 8 -UserAgent 'GameSwitcher').tag_name } catch { 'error' }
    }).AddArgument($AppRepo)
    $script:updateJob = @{ PS = $ps; Handle = $ps.BeginInvoke() }
}

function Receive-UpdateCheck {
    $job = $script:updateJob
    if (-not $job -or -not $job.Handle.IsCompleted) { return }
    $tag = [string]($job.PS.EndInvoke($job.Handle) | Select-Object -First 1)
    $job.PS.Dispose()
    $script:updateJob = $null
    $latest = $null
    try { $latest = [version]($tag.TrimStart('v')) } catch {}
    $text = if (-not $latest) { T 'update.failed' }
            elseif ($latest -gt [version]$AppVersion) { T 'update.available' $tag }
            else { T 'update.none' $AppVersion }
    if ($latest -and $latest -gt [version]$AppVersion) {
        $lnkUpdate.Text = T 'update.available' $tag
        $lnkUpdate.Visible = $true
    }
    if ($script:updateManual -and $script:updateLabel -and -not $script:updateLabel.IsDisposed) { $script:updateLabel.Text = $text }
}

#endregion

#region Settings dialog

$script:bootTime = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToString('o')

function Format-Hotkey([System.Windows.Forms.KeyEventArgs]$E) {
    if ($E.KeyCode -in 'ControlKey', 'ShiftKey', 'Menu', 'LWin', 'RWin') { return $null }
    $parts = @()
    if ($E.Control) { $parts += 'Ctrl' }
    if ($E.Alt) { $parts += 'Alt' }
    if ($E.Shift) { $parts += 'Shift' }
    if (-not $parts) { return $null }   # a hotkey needs at least one modifier
    ($parts + $E.KeyCode.ToString()) -join '+'
}

function Show-SettingsDialog {
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Font = $form.Font; $dlg.StartPosition = 'CenterParent'; $dlg.Icon = $form.Icon
    $dlg.FormBorderStyle = 'FixedDialog'; $dlg.MinimizeBox = $false; $dlg.MaximizeBox = $false; $dlg.ShowInTaskbar = $false
    $dlg.AutoSize = $true; $dlg.AutoSizeMode = 'GrowAndShrink'

    $panel = New-Object System.Windows.Forms.FlowLayoutPanel
    $panel.FlowDirection = 'TopDown'; $panel.WrapContents = $false; $panel.AutoSize = $true; $panel.Padding = '14,10,14,10'
    $panel.MinimumSize = New-Object System.Drawing.Size((Px 520), 0)

    $chkAuto = New-Object System.Windows.Forms.CheckBox; $chkAuto.AutoSize = $true; $chkAuto.Margin = '3,6,3,3'
    $serviceRow = New-Flow; $lblService = New-Label '23,8,8,3'; $btnService = New-Button
    $serviceRow.Controls.AddRange(@($lblService, $btnService))

    $langRow = New-Flow; $lblLang = New-Label '3,14,8,3'
    $cmbLang = New-Object System.Windows.Forms.ComboBox; $cmbLang.DropDownStyle = 'DropDownList'; $cmbLang.Width = Px 200; $cmbLang.Margin = '3,10,3,3'
    $langRow.Controls.AddRange(@($lblLang, $cmbLang))
    $themeRow = New-Flow; $lblTheme = New-Label '3,8,8,3'
    $cmbTheme = New-Object System.Windows.Forms.ComboBox; $cmbTheme.DropDownStyle = 'DropDownList'; $cmbTheme.Width = Px 200; $cmbTheme.Margin = '3,4,3,3'
    $themeRow.Controls.AddRange(@($lblTheme, $cmbTheme))

    $chkNotify = New-Object System.Windows.Forms.CheckBox; $chkNotify.AutoSize = $true; $chkNotify.Margin = '3,14,3,3'
    $chkAutoDetect = New-Object System.Windows.Forms.CheckBox; $chkAutoDetect.AutoSize = $true; $chkAutoDetect.Margin = '3,6,3,3'
    $chkAutoDetect.Tag = [int](Px 500)
    $hotkeyRow = New-Flow
    $chkHotkey = New-Object System.Windows.Forms.CheckBox; $chkHotkey.AutoSize = $true; $chkHotkey.Margin = '3,10,3,3'
    $txtHotkey = New-Object System.Windows.Forms.TextBox; $txtHotkey.Width = Px 130; $txtHotkey.ReadOnly = $true; $txtHotkey.Margin = '6,8,3,3'
    $hotkeyRow.Controls.AddRange(@($chkHotkey, $txtHotkey))
    $lblHotkeyHint = New-Label '23,0,3,3'; $lblHotkeyHint.ForeColor = $colorOff

    $updateRow = New-Flow
    $chkUpdates = New-Object System.Windows.Forms.CheckBox; $chkUpdates.AutoSize = $true; $chkUpdates.Margin = '3,18,3,3'
    $btnCheck = New-Button; $btnCheck.Margin = '12,12,3,3'
    $updateRow.Controls.AddRange(@($chkUpdates, $btnCheck))
    $lblUpdate = New-Label '23,2,3,3'
    $script:updateLabel = $lblUpdate
    $lnkAbout = New-Object System.Windows.Forms.LinkLabel; $lnkAbout.AutoSize = $true; $lnkAbout.Margin = '3,16,3,3'
    $btnClose = New-Button; $btnClose.DialogResult = 'OK'; $btnClose.Margin = '3,16,3,3'
    $dlg.AcceptButton = $btnClose; $dlg.CancelButton = $btnClose

    $panel.Controls.AddRange(@($chkAuto, $serviceRow, $langRow, $themeRow, $chkNotify, $chkAutoDetect, $hotkeyRow, $lblHotkeyHint,
        $updateRow, $lblUpdate, $lnkAbout, $btnClose))
    $dlg.Controls.Add($panel)

    $setTexts = {
        $dlg.Font = $form.Font
        $dlg.Text = T 'settings.title'
        $chkAuto.Text = T 'opt.autostart'
        $lblService.Text = (T 'settings.service') + ' ' + $(if ($script:serviceRunning) { T 'service.running' } else { T 'service.stopped' })
        $btnService.Text = if ($script:serviceRunning) { T 'btn.serviceStop' } else { T 'btn.serviceStart' }
        $lblLang.Text = T 'language.label'
        $lblTheme.Text = T 'opt.theme'
        $chkNotify.Text = T 'opt.notifications'
        $chkAutoDetect.Text = T 'opt.autoDetect'
        $chkHotkey.Text = T 'opt.hotkey'
        $lblHotkeyHint.Text = T 'hotkey.hint'
        $chkUpdates.Text = T 'opt.updates'
        $btnCheck.Text = T 'btn.checkUpdates'
        $lnkAbout.Text = (T 'about.version' $AppVersion) + '   ·   github.com/' + $AppRepo
        $btnClose.Text = T 'btn.close'
        $script:loading = $true
        $cmbTheme.Items.Clear()
        foreach ($t in 'system', 'light', 'dark') {
            $i = New-Object Item; $i.Code = $t; $i.Name = T "theme.$t"
            [void]$cmbTheme.Items.Add($i)
            if ($t -eq $options.Theme) { $cmbTheme.SelectedItem = $i }
        }
        $script:loading = $false
    }

    # Initial values
    $script:loading = $true
    $chkAuto.Checked = [bool](Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)
    foreach ($l in Get-Languages) {
        $i = New-Object Item; $i.Code = $l.Code; $i.Name = $l.Name
        [void]$cmbLang.Items.Add($i)
        if ($l.Code -eq $script:language) { $cmbLang.SelectedItem = $i }
    }
    $chkNotify.Checked = [bool]$options.Notifications
    $chkAutoDetect.Checked = [bool]$options.AutoDetect
    $chkHotkey.Checked = [bool]$options.Hotkey
    $txtHotkey.Text = if ($options.Hotkey) { $options.Hotkey } else { 'Ctrl+Alt+G' }
    $txtHotkey.Enabled = $chkHotkey.Checked
    $chkUpdates.Checked = [bool]$options.CheckUpdates
    $script:loading = $false
    & $setTexts

    $chkAuto.Add_CheckedChanged({
        if ($script:loading) { return }
        try {
            if ($chkAuto.Checked) { & (Join-Path $root 'Install.ps1') -NoStart | Out-Null }
            else { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop }
        } catch {
            [void][System.Windows.Forms.MessageBox]::Show($dlg, (T 'dlg.autostartError' $_.Exception.Message), (T 'opt.autostart'))
        }
    })
    $btnService.Add_Click({
        $dlg.Cursor = 'WaitCursor'
        $procs = @(Get-WatcherProcesses)
        if ($procs) { foreach ($p in $procs) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue } }
        else { Start-Watcher }
        Start-Sleep -Milliseconds 1500
        Update-ServiceState
        $dlg.Cursor = 'Default'
        & $setTexts
        Update-Status
    })
    $cmbLang.Add_SelectedIndexChanged({
        if ($script:loading -or -not $cmbLang.SelectedItem) { return }
        Initialize-Language $cmbLang.SelectedItem.Code
        Save-All
        Update-Language
        & $setTexts
        Update-CheckSizes $dlg
    })
    $cmbTheme.Add_SelectedIndexChanged({
        if ($script:loading -or -not $cmbTheme.SelectedItem) { return }
        $options.Theme = $cmbTheme.SelectedItem.Code
        Save-All
        Set-Theme
        Set-Theme $dlg
    })
    $chkNotify.Add_CheckedChanged({ if (-not $script:loading) { $options.Notifications = $chkNotify.Checked; Save-All } })
    $chkAutoDetect.Add_CheckedChanged({ if (-not $script:loading) { $options.AutoDetect = $chkAutoDetect.Checked; Save-All } })
    $chkHotkey.Add_CheckedChanged({
        if ($script:loading) { return }
        $txtHotkey.Enabled = $chkHotkey.Checked
        $options.Hotkey = if ($chkHotkey.Checked) { $txtHotkey.Text } else { '' }
        Save-All
    })
    $txtHotkey.Add_KeyDown({
        param($s, $e)
        $e.SuppressKeyPress = $true
        $text = Format-Hotkey $e
        if ($text) { $txtHotkey.Text = $text; $options.Hotkey = $text; Save-All }
    })
    $chkUpdates.Add_CheckedChanged({ if (-not $script:loading) { $options.CheckUpdates = $chkUpdates.Checked; Save-All } })
    $btnCheck.Add_Click({ $lblUpdate.Text = T 'update.checking'; Start-UpdateCheck -Manual })
    $lnkAbout.Add_LinkClicked({ Start-Process "https://github.com/$AppRepo" })

    Set-Theme $dlg
    [void]$dlg.ShowDialog($form)
    $script:updateLabel = $null
    $dlg.Dispose()
}

function Start-Watcher {
    $exe = Join-Path $root 'GameSwitcher.exe'
    if (Test-Path $exe) { Start-Process $exe -ArgumentList '--watcher' }
    else {
        # conhost --headless: no console window, even with Windows Terminal as the default
        Start-Process conhost.exe -ArgumentList "--headless powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File `"$(Join-Path $root 'Watcher.ps1')`""
    }
}

#endregion

#region Events

$list.Add_ItemChecked({
    param($sender, $e)
    if ($script:loading) { return }
    $game = $e.Item.Tag
    if ($e.Item.Checked) {
        $selected[$game.Key] = [pscustomobject]@{ Key = $game.Key; Name = $game.Name; Exes = @($game.Exes) }
    } else {
        $selected.Remove($game.Key)
    }
    $e.Item.SubItems[2].Text = if (-not $e.Item.Checked) { '' } elseif ($profiles.ContainsKey($game.Key)) { T 'profile.ownShort' } else { T 'profile.defaultShort' }
    Save-All
    Update-ProfileList
    Update-Status
    Update-Running
})
$list.Add_SelectedIndexChanged({
    Update-RemoveButton
    $game = Get-SelectedGame
    if ($game -and $selected.ContainsKey($game.Key)) { Select-Profile $game.Key } elseif ($game) { Select-Profile '' }
})
$list.Add_ColumnClick({
    param($s, $e)
    if ($sorter.Column -eq $e.Column) { $sorter.Descending = -not $sorter.Descending }
    else { $sorter.Column = $e.Column; $sorter.Descending = $e.Column -in 3, 4 }   # play time and date: largest first
    $list.Sort()
})
$list.Add_DoubleClick({ $g = Get-SelectedGame; if ($g) { Start-Game $g } })

$menu.Add_Opening({
    param($sender, $e)
    $game = Get-SelectedGame
    if (-not $game) { $e.Cancel = $true; return }
    $miLaunch.Enabled = [bool]($game.Launch -or $game.MainExe)
    $miOpen.Enabled = [bool]$game.Path -and (Test-Path $game.Path)
    $miRemove.Enabled = $removableSources -contains $game.Source
})
$miLaunch.Add_Click({ Start-Game (Get-SelectedGame) })
$miProfile.Add_Click({
    $game = Get-SelectedGame
    if (-not $selected.ContainsKey($game.Key)) { $list.SelectedItems[0].Checked = $true }
    Select-Profile $game.Key
    if (-not $profiles.ContainsKey($game.Key)) { $chkOwnProfile.Checked = $true }
})
$miOpen.Add_Click({
    $game = Get-SelectedGame
    if (Test-Path $game.Path -PathType Leaf) { Start-Process explorer.exe -ArgumentList "/select,`"$($game.Path)`"" }
    else { Start-Process explorer.exe -ArgumentList "`"$($game.Path)`"" }
})
$miRemove.Add_Click({ Remove-SelectedGame })

$txtSearch.Add_TextChanged({ Show-Games })
$cmbSource.Add_SelectedIndexChanged({ if (-not $script:loading) { Show-Games } })
$chkOnlySelected.Add_CheckedChanged({ Show-Games })
$btnRescan.Add_Click({ Invoke-Scan })
$btnRemove.Add_Click({ Remove-SelectedGame })
$btnSettings.Add_Click({ Show-SettingsDialog })
$btnLog.Add_Click({
    if (Test-Path $logFile) { Start-Process notepad.exe -ArgumentList "`"$logFile`"" }
    else { [void][System.Windows.Forms.MessageBox]::Show($form, (T 'dlg.noLog'), (T 'btn.log')) }
})
$lnkUpdate.Add_LinkClicked({ Start-Process "https://github.com/$AppRepo/releases/latest" })

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

$btnPause.Add_Click({
    if (Get-Pause) { Remove-Item $pausePath -ErrorAction SilentlyContinue; Update-PauseLabel }
    else { $pauseMenu.Show($btnPause, 0, $btnPause.Height) }
})
$miPause1.Add_Click({ Set-Pause -Hours 1 })
$miPause2.Add_Click({ Set-Pause -Hours 2 })
$miPauseReboot.Add_Click({ Set-Pause -UntilReboot })

$cmbProfile.Add_SelectedIndexChanged({
    if ($script:loading -or -not $cmbProfile.SelectedItem) { return }
    $script:editKey = $cmbProfile.SelectedItem.Code
    Show-Actions
})
$chkOwnProfile.Add_CheckedChanged({
    if ($script:loading -or -not $script:editKey) { return }
    if ($chkOwnProfile.Checked) { $profiles[$script:editKey] = Copy-Actions $actions }
    else { $profiles.Remove($script:editKey) }
    Save-All
    Show-Actions
    foreach ($item in $list.Items) {
        if ($item.Tag.Key -eq $script:editKey -and $item.Checked) {
            $item.SubItems[2].Text = if ($chkOwnProfile.Checked) { T 'profile.ownShort' } else { T 'profile.defaultShort' }
        }
    }
})

foreach ($b in $bindings) {
    switch ($b[0].GetType().Name) {
        'CheckBox' { $b[0].Add_CheckedChanged({ Save-Actions }) }
        'NumericUpDown' { $b[0].Add_ValueChanged({ Save-Actions }) }
        'TextBox' { $b[0].Add_Leave({ Save-Actions }) }
    }
}
foreach ($c in $cmbAudio, $cmbMic, $cmbDisplay, $cmbPower) { $c.Add_SelectedIndexChanged({ Save-Actions }) }
$cmbAudio.Add_DropDown({ $script:loading = $true; Update-DeviceCombo $cmbAudio 0 (Get-EditActions).Actions.Audio; $script:loading = $false })
$cmbMic.Add_DropDown({ $script:loading = $true; Update-DeviceCombo $cmbMic 1 (Get-EditActions).Actions.Mic; $script:loading = $false })

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
foreach ($row in $scriptStart, $scriptEnd) {
    $row.Browse.Tag = $row.Text
    $row.Browse.Add_Click({
        $dlg = New-Object System.Windows.Forms.OpenFileDialog
        $dlg.Title = T 'dlg.chooseScript'; $dlg.Filter = T 'dlg.scriptFilter'
        if ($dlg.ShowDialog($form) -eq 'OK') { $this.Tag.Text = "`"$($dlg.FileName)`""; Save-Actions }
    })
}

$btnAddExe.Add_Click({
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Title = T 'dlg.chooseGame'; $dlg.Filter = T 'dlg.exeFilter'
    if ($dlg.ShowDialog($form) -ne 'OK') { return }
    if (-not ($customExes -contains $dlg.FileName)) { [void]$customExes.Add($dlg.FileName) }
    $game = New-ExeGame $dlg.FileName
    $selected[$game.Key] = [pscustomobject]@{ Key = $game.Key; Name = $game.Name; Exes = @($game.Exes) }
    $script:games = @(@($games | Where-Object { $_.Key -ne $game.Key }) + $game | Sort-Object Name)
    Save-All
    Update-SourceFilter
    Update-ProfileList
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
    Update-SourceFilter
    Show-Games
    [void][System.Windows.Forms.MessageBox]::Show($form, (T 'dlg.gamesFound' $new.Count), (T 'btn.addFolder'))
})

# Switch the language at runtime: re-apply all texts
function Update-Language {
    Set-UiTexts
    Update-Hdr
    Update-Running
    Update-AudioLabel
    Update-PauseLabel
    Update-SourceFilter
    Update-ProfileList
    Show-Games
    Set-Theme
}

# Refresh the display every 2 s, the service state and play time every 10 s
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 2000
$timer.Add_Tick({
    Update-Hdr
    Update-Running
    Update-AudioLabel
    Update-PauseLabel
    Receive-UpdateCheck
    $script:tick++
    if ($script:tick % 5 -eq 0) { Update-ServiceState; Update-Status; Update-PlaytimeColumns }
})

Set-UiTexts
$form.Add_Shown({
    Set-Theme
    Update-AudioLabel
    Update-Hdr
    Update-PauseLabel
    Update-ServiceState
    Invoke-Scan
    Update-Running
    if ($options.CheckUpdates) { Start-UpdateCheck }
    $timer.Start()
})
$form.Add_FormClosed({ $timer.Stop() })
[void]$form.ShowDialog()

#endregion
