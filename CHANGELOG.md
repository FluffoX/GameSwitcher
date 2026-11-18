# Changelog

## 2.5.0

Everything new is optional and can be turned off in the settings; whatever changes the system is off until you turn it on.

- **Battery levels** of controllers of any brand (XInput, PlayStation, Nintendo Switch), Logitech wireless devices (mice, keyboards, headsets; all four HID++ battery features) and any device that reports its battery the standard way (HID battery value or Bluetooth) – shown in the window and the tray menu, with a warning when a battery drops below a threshold (default 20 %) and when a game starts with a weak battery.
- **Graphics per game:** use the high-performance graphics card and turn Auto HDR on or off per game. GameSwitcher writes the per-program entries Windows uses (Settings → Display → Graphics) and restores the previous ones when the option is turned off.
- **Monitor brightness** as a new action (external monitors via DDC/CI, built-in laptop screens via WMI), restored after the game.
- **Couch mode:** when a controller of any brand connects, switch to the TV (second screen only, duplicate or extend), its audio output and run a command; switch back when the controller is off. Controllers that are always plugged in (wheels, flight sticks) are ignored.
- **Start through GameSwitcher:** starting a game from the list, the tray menu or a new desktop shortcut applies its profile before the game starts (for games that check HDR or the resolution at startup).
- **HDR on one chosen monitor** instead of all HDR-capable monitors.
- **Mouse and keyboard actions:** pointer acceleration off, pointer speed, accessibility shortcuts off (Shift five times), Windows key blocked while the game is in the foreground – each restored after the game.
- **Recently played** games in the tray menu, launchable with one click (with the game's profile).
- **Session summary:** after a game, a notification shows how long you played.
- **Statistics:** play time per day or month (last 7 days, 30 days, 12 months), per game. Play time per day is recorded from this version on.
- While couch mode is on, game profiles leave monitors, resolution, brightness and audio output alone (optional).
- The settings window is now organized in two columns (general and extras); the actions panel has collapsible sections that show how many actions are on. Every option has a short explanation with an example below it and as a tooltip (can be turned off).

## 2.4.0

- **The window is now C# as well:** GameSwitcher no longer contains any PowerShell. Same window, same features, same settings – it just starts faster and needs less memory.
- **Tray icon as HDR switch (optional, in the settings):** a click on the tray icon toggles HDR, and the icon shows the state – colored with a green dot = HDR on, gray = HDR off. The window then opens via the tray menu.
- **Clean shutdown:** "Stop service", updates and uninstalling now ask the background service to revert its changes and exit, instead of ending it forcibly.
- `GameSwitcher.exe --install` / `--uninstall` / `--stop` for autostart, removal and stopping the service; the uninstaller uses them.
- The program folder only contains `GameSwitcher.exe`, the translations and the icon.

## 2.3.0

- **Background service rewritten in C#:** about half the memory (≈ 35 MB instead of ≈ 70 MB private memory) and a fraction of the CPU time of the PowerShell version, fewer threads, and no PowerShell running in the background – which also makes antivirus false alarms less likely.
- Still a single small `GameSwitcher.exe` (about 110 KB) on the .NET Framework built into Windows – no runtime to install.
- Same settings, profiles, translations and crash-safe state as before; updating keeps everything. The window is unchanged (still PowerShell for now).
- Building no longer needs any packages or internet (.NET SDK only).

## 2.2.0

- **Setup program:** `GameSwitcher-Setup-<version>.exe` installs GameSwitcher per user (no administrator rights), adds a Start menu entry and an entry under Windows Settings → Apps with a proper uninstaller. Running a newer setup updates in place.
- **Settings moved to `%AppData%\GameSwitcher`:** settings, play time and logs are no longer stored next to the program, so updates and uninstalling never touch them. Existing files are moved there automatically.
- **Tidy program folder:** only `GameSwitcher.exe` and the documents at the top, everything else in `app\`; build tools are no longer part of the download.
- **Version information** in `GameSwitcher.exe` (file properties) and **SHA-256 checksums** for every release.
- **Automated builds:** every push is checked by GitHub Actions; releases are built and published from the tagged source.
- Fixed: some spacing values in the window were not applied.

## 2.1.0

- **Setup on first start:** unpack the ZIP and start GameSwitcher.exe – the app asks whether it should start with Windows and sets up autostart and a Start menu entry itself. No PowerShell command needed anymore.
- Opening GameSwitcher always makes sure the background service is running.

## 2.0.0

- **Per-game profiles:** a default profile for all games, and any game can get its own settings.
- **New actions:** SDR brightness in HDR, resolution / refresh rate, turn off other monitors, night light (where supported), microphone, volume, power plan, prevent display standby, and custom commands before and after the game.
- **Tray icon** with status, pause (1 h, 2 h, until restart), HDR toggle and manual game mode.
- **Hotkey** for a manual game mode (default Ctrl+Alt+G), e.g. for emulators or browser games.
- **Automatic detection** (optional) of games Windows recognizes via Game Bar, without having to check them.
- **Notifications** when settings are applied and restored.
- **Faster reaction:** games are detected within about a second.
- **Crash-safe:** changes are recorded right away; after a crash or power loss they are reverted on the next start.
- **Manual changes win:** volume and display mode are only restored if you did not change them during the game.
- **Game list:** icons, filter by source, play time and last played (sortable), launch games directly (Steam, Epic, Ubisoft via their launchers).
- **Settings window** with autostart, service, language, design, notifications, hotkey and update check.
- **Dark mode** (follows Windows or set manually).
- **Update check** against GitHub releases.
- **GameSwitcher.exe:** runs without any console window and shows up as "GameSwitcher" in Task Manager; Start menu shortcut.

## 1.1.0

- 12 languages, switchable at runtime.

## 1.0.0

- HDR, audio output and closing/starting programs when a selected game starts, optionally reverted afterwards.
- Automatic game discovery for Steam, Epic, GOG, Ubisoft, Xbox and EA.
