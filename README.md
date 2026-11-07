# GameSwitcher

**English** | [Deutsch](README.de.md)

Automatically switches Windows 11 settings as soon as a selected game starts – and, if you want, switches them back when the game exits:

- **HDR** on/off
- **Audio output** to a specific device (e.g. headset)
- **Close programs** (e.g. Wallpaper Engine, browser) and restart them afterwards
- **Start programs** (e.g. Discord) and close them again afterwards

Installed games from **Steam, Epic, GOG, Ubisoft, Xbox/Game Pass and EA** are found automatically. Custom games or whole game folders can be added.

Pure PowerShell (Windows PowerShell 5.1), no additional software required.

**Languages:** English, Deutsch, Español, Français, Italiano, Português, Polski, Türkçe, Русский, 中文（简体）, 日本語, 한국어. On first start the Windows display language is used; it can be changed at any time at the bottom of the window.

## Setup

1. Download or clone the repository.
2. Run once (creates the `GameSwitcher.lnk` shortcut and the autostart task):
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install.ps1
   ```
3. Open `GameSwitcher.lnk`, check your games and choose on the right what should happen when a game starts.

Uninstall: `.\Install.ps1 -Uninstall`

## How it works

A background service (`Watcher.ps1`) checks every 3 seconds whether one of the selected games is running. It is started at logon via Task Scheduler and runs through `conhost --headless` without any window.

| File | Purpose |
|---|---|
| `GameSwitcher.ps1` | GUI: game selection, actions, HDR/audio status, autostart |
| `Watcher.ps1` | Background service |
| `Actions.ps1` | Actions on game start/exit and reverting them |
| `HdrControl.ps1` | HDR via the DisplayConfig API (`.\HdrControl.ps1 status\|on\|off`) |
| `AudioControl.ps1` | Default audio device via Core Audio (`.\AudioControl.ps1 list`) |
| `Install.ps1` | Set up/remove the autostart task and shortcut |
| `Strings.ps1`, `lang/*.json` | Translations (one file per language) |

### Adding a language

Copy `lang/en.json`, name it after the two-letter language code (e.g. `nl.json`), set `_name` to the language's own name for itself and translate the texts. Placeholders such as `{0}` must be kept. Missing entries automatically fall back to English. For scripts with their own characters, `_font` can name a suitable Windows font.

Files created locally (not part of the repository): `settings.json` (selection and options), `games.txt` (watched processes), `watcher.log` (log).

## Notes

- Only what the service changed itself is reverted. If you switch HDR or the audio device manually during a game, your choice is kept.
- Switching the audio device uses the undocumented but stable (since Windows 7) `IPolicyConfig` interface.
- Restarted programs start without their original command-line arguments.

## License

[MIT](LICENSE) – free to use, modify and redistribute as long as the license notice is kept.
