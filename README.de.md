# GameSwitcher

[English](README.md) | **Deutsch**

Schaltet unter Windows 11 automatisch um, sobald du ein Spiel startest – und auf Wunsch nach dem Spielende alles wieder zurück.

![GameSwitcher](assets/screenshot.png)

## Funktionen

**Beim Spielstart (jede Aktion lässt sich danach zurücksetzen)**

| Anzeige | Audio | System | Programme |
|---|---|---|---|
| HDR an | Audioausgabe (z. B. Headset) | Energiesparplan | Programme beenden (z. B. Wallpaper Engine) |
| SDR-Helligkeit im HDR | Mikrofon | Bildschirm-Standby verhindern | Programme starten (z. B. Discord) |
| Auflösung / Bildwiederholrate | Lautstärke | | Eigene Befehle vor und nach dem Spiel |
| Weitere Monitore ausschalten | | | |
| Nachtlicht aus ¹ | | | |

**Profile pro Spiel** – ein Standardprofil für alle Spiele; jedes Spiel kann eigene Einstellungen bekommen.

**Spielerkennung**
- Installierte Spiele aus **Steam, Epic, GOG, Ubisoft, Xbox/Game Pass und EA** werden automatisch gefunden, dazu Spiele, die Windows über die Game Bar kennt. Eigene Spiele oder ganze Spieleordner lassen sich ergänzen.
- Optional reagiert GameSwitcher auf jedes Spiel, das Windows erkennt, ohne es vorher anzuhaken.
- **Tastenkürzel** (Standard Strg+Alt+G) für einen manuellen Spielmodus, z. B. für Emulatoren oder Browserspiele.
- Reagiert innerhalb von etwa einer Sekunde.

**Im Alltag**
- **Symbol im Infobereich** mit Status, Pause (1 Std., 2 Std., bis zum Neustart), HDR-Umschalter und manuellem Spielmodus.
- **Benachrichtigungen**, wenn Einstellungen übernommen und zurückgesetzt werden.
- **Spielzeit** und „Zuletzt gespielt“ pro Spiel; Spiele direkt aus der Liste starten.
- **Absturzsicher:** Jede Änderung wird sofort vermerkt und nach einem Absturz oder Stromausfall beim nächsten Start zurückgesetzt.
- **Deine Änderungen haben Vorrang:** Was du während des Spiels selbst umstellst, wird nicht zurückgesetzt.
- **12 Sprachen:** English, Deutsch, Español, Français, Italiano, Português, Polski, Türkçe, Русский, 中文（简体）, 日本語, 한국어.
- **Dark Mode**, wie Windows oder fest eingestellt.
- **Update-Prüfung** gegen die GitHub-Releases (abschaltbar).

¹ Nur auf PCs, auf denen Windows den Nachtlicht-Zustand im bekannten Format speichert; sonst wird die Option als nicht unterstützt angezeigt.

<details><summary>Dark Mode</summary>

![GameSwitcher im Dark Mode](assets/screenshot-dark.png)
</details>

## Installation

1. `GameSwitcher-<version>.zip` unter [Releases](https://github.com/FluffoX/GameSwitcher/releases) herunterladen und in einen beliebigen Ordner entpacken (z. B. `C:\Tools\GameSwitcher`).
2. **GameSwitcher.exe** starten. Beim ersten Start fragt GameSwitcher, ob es mit Windows starten soll – mit **Ja** beantworten. Damit werden Autostart und ein Eintrag im Startmenü eingerichtet.
3. Spiele anhaken und rechts festlegen, was beim Spielstart passieren soll. Fertig.

> Beim ersten Start kann Windows *„Der Computer wurde durch Windows geschützt“* anzeigen, weil das Programm nicht code-signiert ist. Auf **Weitere Informationen → Trotzdem ausführen** klicken.

Der Autostart lässt sich später unter **Einstellungen…** ein- und ausschalten. Zum vollständigen Entfernen dort den Autostart abschalten und den Ordner löschen (oder `.\Install.ps1 -Uninstall` ausführen).

**Aus dem Quellcode:** Repository klonen und einmalig ausführen:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install.ps1
```

`Install.ps1` baut `GameSwitcher.exe` mit dem C#-Compiler, der in Windows enthalten ist, und richtet Autostart und Startmenü-Eintrag ein.

## Funktionsweise

Der Hintergrunddienst (`GameSwitcher.exe --watcher`, bei der Anmeldung über die Aufgabenplanung gestartet) prüft jede Sekunde, ob ein ausgewähltes Spiel läuft, wendet die Aktionen seines Profils an und setzt sie etwa 10 Sekunden nach Spielende zurück (damit Launcher-Neustarts kein Hin und Her auslösen). Alles ist reines PowerShell (Windows PowerShell 5.1), ausgeführt von einem kleinen C#-Starter – deshalb erscheint nie ein Konsolenfenster, und im Task-Manager steht „GameSwitcher“.

| Datei | Aufgabe |
|---|---|
| `GameSwitcher.ps1` | Oberfläche: Spieleliste, Profile, Aktionen, Einstellungen |
| `Watcher.ps1` | Hintergrunddienst mit Tray-Symbol, Erkennung, Spielzeit |
| `Actions.ps1` | Aktionen bei Spielstart/-ende, Profile, absturzsicherer Zustand |
| `HdrControl.ps1` | HDR, SDR-Helligkeit und Monitore über die DisplayConfig-API |
| `AudioControl.ps1` | Audioausgabe, Mikrofon und Lautstärke über Core Audio |
| `SystemControl.ps1` | Anzeigemodus, Energiesparpläne, Standby, Nachtlicht |
| `Strings.ps1`, `lang/*.json` | Übersetzungen (eine Datei pro Sprache) |
| `src/Launcher.cs`, `Build.ps1` | `GameSwitcher.exe` |
| `Install.ps1` | Autostart-Aufgabe und Verknüpfungen |
| `Release.ps1` | Release-ZIP und winget-Manifeste |

Lokal erzeugte Dateien (nicht im Repository): `settings.json`, `playtime.json`, `state.json` (nur während eines Spiels), `pause.json`, `watcher.log`.

### Weitere Sprache hinzufügen

`lang/en.json` kopieren, nach dem zweistelligen Sprachcode benennen (z. B. `nl.json`), `_name` auf den Namen der Sprache in der Sprache selbst setzen und die Texte übersetzen. Platzhalter wie `{0}` müssen erhalten bleiben. Fehlende Einträge werden automatisch auf Englisch angezeigt. Für Schriften mit eigenen Zeichen kann `_font` eine passende Windows-Schrift angeben.

## Hinweise

- Einige Funktionen nutzen undokumentierte, aber seit Langem stabile Windows-Schnittstellen: Audiogerät umschalten (`IPolicyConfig`), SDR-Helligkeit und Nachtlicht. Ändert Windows sie, funktioniert die betroffene Aktion nicht mehr; alles andere läuft weiter.
- „Weitere Monitore ausschalten“ behält den Monitor mit dem Hauptdesktop und stellt danach die zuletzt von Windows gemerkte erweiterte Anordnung wieder her.
- Wieder gestartete Programme starten ohne ihre ursprünglichen Startparameter.
- `GameSwitcher.exe` ist nicht code-signiert. Manche Virenscanner sind bei Programmen, die PowerShell ausführen, vorsichtig; der vollständige Quellcode liegt in `src/Launcher.cs`.
- Die Update-Prüfung kontaktiert nur `api.github.com`.

## Lizenz

[MIT](LICENSE) – frei nutzbar, veränderbar und weitergebbar, solange der Lizenzhinweis erhalten bleibt.
