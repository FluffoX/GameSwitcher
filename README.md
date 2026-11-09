# GameSwitcher

[![CI](https://github.com/FluffoX/GameSwitcher/actions/workflows/ci.yml/badge.svg)](https://github.com/FluffoX/GameSwitcher/actions/workflows/ci.yml) [![Release](https://img.shields.io/github/v/release/FluffoX/GameSwitcher)](https://github.com/FluffoX/GameSwitcher/releases/latest) [![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**English** | [Deutsch](README.de.md)

Automatically switches Windows 11 settings as soon as you start a game – and, if you want, switches everything back when the game exits.

![GameSwitcher](assets/screenshot.png)

## Features

**On game start (each action can be reverted afterwards)**

| Display | Audio | System | Programs |
|---|---|---|---|
| HDR on | Audio output (e.g. headset) | Power plan | Close programs (e.g. Wallpaper Engine) |
| SDR brightness in HDR | Microphone | Prevent display standby | Start programs (e.g. Discord) |
| Resolution / refresh rate | Volume | | Custom commands before and after the game |
| Turn off other monitors | | | |
| Night light off ¹ | | | |

**Per-game profiles** – one default profile for all games; any game can get its own settings.

**Game detection**
- Installed games from **Steam, Epic, GOG, Ubisoft, Xbox/Game Pass and EA** are found automatically, plus games Windows knows from Game Bar. Custom games or whole game folders can be added.
- Optionally reacts to every game Windows recognizes, without checking it first.
- **Hotkey** (default Ctrl+Alt+G) for a manual game mode, e.g. for emulators or browser games.
- Reacts within about a second.

**Everyday use**
- **Tray icon** with status, pause (1 h, 2 h, until restart), HDR toggle and manual game mode.
- **Notifications** when settings are applied and restored.
- **Play time** and last played per game; launch games directly from the list.
- **Crash-safe:** every change is recorded right away and reverted on the next start after a crash or power loss.
- **Your manual changes win:** settings you change yourself during a game are not reset.
- **12 languages:** English, Deutsch, Español, Français, Italiano, Português, Polski, Türkçe, Русский, 中文（简体）, 日本語, 한국어.
- **Dark mode**, following Windows or set manually.
- **Update check** against GitHub releases (can be turned off).

¹ Only on PCs where Windows stores the night light state in the known format; otherwise the option is shown as unsupported.

<details><summary>Dark mode</summary>

![GameSwitcher in dark mode](assets/screenshot-dark.png)
</details>

## Installation

1. Download **`GameSwitcher-Setup-<version>.exe`** from [Releases](https://github.com/FluffoX/GameSwitcher/releases/latest) and run it. No administrator rights are needed; GameSwitcher is installed to `%LocalAppData%\Programs\GameSwitcher`.
2. On the first start GameSwitcher asks whether it should start with Windows – answer **Yes**.
3. Check your games and choose on the right what should happen when a game starts. Done.

> Windows may show *"Windows protected your PC"* because the program is not code-signed. Click **More info → Run anyway**.

**Update:** run the setup of the new version – it replaces the old one and keeps all settings. GameSwitcher tells you in its window when a new version is available.

**Uninstall:** Windows Settings → Apps → GameSwitcher → Uninstall. This also removes autostart and the Start menu entry. Your settings in `%AppData%\GameSwitcher` are kept; delete that folder if you don't need them anymore.

**Portable:** prefer no installation? Download `GameSwitcher-<version>.zip`, extract it anywhere and start `GameSwitcher.exe` – it asks about autostart the same way.

Every release lists the SHA-256 checksums of its files in `SHA256SUMS.txt`.

## How it works

The background service (`GameSwitcher.exe --watcher`, started at logon via Task Scheduler) checks every second whether a selected game is running, applies the actions of its profile and reverts them about 10 seconds after the game exits (so launcher restarts don't cause flickering). Everything is plain PowerShell (Windows PowerShell 5.1) hosted by a small C# launcher, so no console window ever appears and Task Manager shows "GameSwitcher".

Settings, play time and logs are stored in `%AppData%\GameSwitcher` (`settings.json`, `playtime.json`, `watcher.log`; `state.json` only during a game).

| Path | Purpose |
|---|---|
| `app/GameSwitcher.ps1` | GUI: game list, profiles, actions, settings |
| `app/Watcher.ps1` | Background service with tray icon, detection, play time |
| `app/Actions.ps1` | Actions on game start/exit, profiles, crash-safe state |
| `app/HdrControl.ps1` | HDR, SDR brightness and monitors via the DisplayConfig API |
| `app/AudioControl.ps1` | Audio output, microphone and volume via Core Audio |
| `app/SystemControl.ps1` | Display mode, power plans, standby, night light |
| `app/Install.ps1` | Autostart task and Start menu entry (used by the app and the uninstaller) |
| `app/Strings.ps1`, `app/lang/*.json` | Translations (one file per language) |
| `src/Launcher.cs`, `Build.ps1` | `GameSwitcher.exe` |
| `installer/GameSwitcher.iss`, `Release.ps1` | Setup (Inno Setup), portable ZIP and checksums |
| `.github/workflows/` | Checks on every push; releases are built and published by GitHub Actions when a version tag is pushed |

### Building from source

```powershell
powershell -ExecutionPolicy Bypass -File .\app\Install.ps1
```

builds `GameSwitcher.exe` with the C# compiler that ships with Windows and sets up autostart. `.\Release.ps1` builds the setup and ZIP locally (needs [Inno Setup 6](https://jrsoftware.org/isinfo.php)); `.\tools\Test-Project.ps1` runs the same checks as GitHub Actions.

### Adding a language

Copy `app/lang/en.json`, name it after the two-letter language code (e.g. `nl.json`), set `_name` to the language's own name for itself and translate the texts. Placeholders such as `{0}` must be kept. Missing entries automatically fall back to English. For scripts with their own characters, `_font` can name a suitable Windows font.
## Notes

- Some functions use undocumented but long-stable Windows interfaces: switching the audio device (`IPolicyConfig`), SDR brightness and night light. If Windows changes them, the affected action stops working; everything else keeps running.
- "Turn off other monitors" keeps the monitor showing the main desktop and afterwards restores the last extended layout Windows remembers.
- Restarted programs start without their original command-line arguments.
- `GameSwitcher.exe` is not code-signed. Some antivirus programs are wary of executables that host PowerShell; the full source is in `src/Launcher.cs`.
- The update check only contacts `api.github.com`.

## License

[MIT](LICENSE) – free to use, modify and redistribute as long as the license notice is kept.
