# Changelog

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
