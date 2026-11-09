# GameSwitcher

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

**Release (recommended):** download `GameSwitcher-<version>.zip` from [Releases](https://github.com/FluffoX/GameSwitcher/releases), extract it to a folder of your choice and run once:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install.ps1
```

This sets up the background service at logon and adds GameSwitcher to the Start menu. Then open **GameSwitcher**, check your games and choose on the right what should happen when a game starts.

**From source:** clone the repository and run the same command – `Install.ps1` builds `GameSwitcher.exe` with the C# compiler that ships with Windows.

Uninstall: `.\Install.ps1 -Uninstall`

## How it works

The background service (`GameSwitcher.exe --watcher`, started at logon via Task Scheduler) checks every second whether a selected game is running, applies the actions of its profile and reverts them about 10 seconds after the game exits (so launcher restarts don't cause flickering). Everything is plain PowerShell (Windows PowerShell 5.1) hosted by a small C# launcher, so no console window ever appears and Task Manager shows "GameSwitcher".

| File | Purpose |
|---|---|
| `GameSwitcher.ps1` | GUI: game list, profiles, actions, settings |
| `Watcher.ps1` | Background service with tray icon, detection, play time |
| `Actions.ps1` | Actions on game start/exit, profiles, crash-safe state |
| `HdrControl.ps1` | HDR, SDR brightness and monitors via the DisplayConfig API |
| `AudioControl.ps1` | Audio output, microphone and volume via Core Audio |
| `SystemControl.ps1` | Display mode, power plans, standby, night light |
| `Strings.ps1`, `lang/*.json` | Translations (one file per language) |
| `src/Launcher.cs`, `Build.ps1` | `GameSwitcher.exe` |
| `Install.ps1` | Autostart task and shortcuts |
| `Release.ps1` | Release ZIP and winget manifests |

Files created locally (not part of the repository): `settings.json`, `playtime.json`, `state.json` (only during a game), `pause.json`, `watcher.log`.

### Adding a language

Copy `lang/en.json`, name it after the two-letter language code (e.g. `nl.json`), set `_name` to the language's own name for itself and translate the texts. Placeholders such as `{0}` must be kept. Missing entries automatically fall back to English. For scripts with their own characters, `_font` can name a suitable Windows font.

## Notes

- Some functions use undocumented but long-stable Windows interfaces: switching the audio device (`IPolicyConfig`), SDR brightness and night light. If Windows changes them, the affected action stops working; everything else keeps running.
- "Turn off other monitors" keeps the monitor showing the main desktop and afterwards restores the last extended layout Windows remembers.
- Restarted programs start without their original command-line arguments.
- `GameSwitcher.exe` is not code-signed. Some antivirus programs are wary of executables that host PowerShell; the full source is in `src/Launcher.cs`.
- The update check only contacts `api.github.com`.

## License

[MIT](LICENSE) – free to use, modify and redistribute as long as the license notice is kept.
