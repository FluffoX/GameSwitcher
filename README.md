# GameSwitcher

Schaltet unter Windows 11 automatisch um, sobald ein ausgewähltes Spiel startet – und auf Wunsch nach dem Spielende wieder zurück:

- **HDR** an/aus
- **Audioausgabe** auf ein bestimmtes Gerät (z. B. Headset)
- **Programme beenden** (z. B. Wallpaper Engine, Browser) und danach wieder starten
- **Programme starten** (z. B. Discord) und danach wieder beenden

Installierte Spiele aus **Steam, Epic, GOG, Ubisoft, Xbox/Game Pass und EA** werden automatisch gefunden. Eigene Spiele oder ganze Spieleordner lassen sich ergänzen.

Reines PowerShell (Windows PowerShell 5.1), keine Installation zusätzlicher Software nötig.

**Sprachen:** English, Deutsch, Español, Français, Italiano, Português, Polski, Türkçe, Русский, 中文（简体）, 日本語, 한국어. Beim ersten Start wird die Windows-Anzeigesprache verwendet; umstellen lässt sie sich jederzeit unten im Fenster.

## Einrichtung

1. Repository herunterladen oder klonen.
2. Einmalig ausführen (legt die Verknüpfung `GameSwitcher.lnk` und den Autostart an):
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install.ps1
   ```
3. `GameSwitcher.lnk` öffnen, Spiele anhaken und rechts festlegen, was beim Spielstart passieren soll.

Deinstallieren: `.\Install.ps1 -Uninstall`

## Funktionsweise

Ein Hintergrunddienst (`Watcher.ps1`) prüft alle 3 Sekunden, ob eines der ausgewählten Spiele läuft. Er wird über die Aufgabenplanung bei der Anmeldung gestartet und läuft über `conhost --headless` ganz ohne Fenster.

| Datei | Aufgabe |
|---|---|
| `GameSwitcher.ps1` | Oberfläche: Spieleauswahl, Aktionen, HDR-/Audio-Anzeige, Autostart |
| `Watcher.ps1` | Hintergrunddienst |
| `Actions.ps1` | Aktionen beim Spielstart/-ende und deren Zurücksetzen |
| `HdrControl.ps1` | HDR über die DisplayConfig-API (`.\HdrControl.ps1 status\|on\|off`) |
| `AudioControl.ps1` | Standard-Audiogerät über Core Audio (`.\AudioControl.ps1 list`) |
| `Install.ps1` | Autostart-Aufgabe und Verknüpfung einrichten/entfernen |
| `Strings.ps1`, `lang/*.json` | Übersetzungen (eine Datei pro Sprache) |

### Weitere Sprache hinzufügen

`lang/en.json` kopieren, nach dem zweistelligen Sprachcode benennen (z. B. `nl.json`), `_name` auf den Namen der Sprache in der Sprache selbst setzen und die Texte übersetzen. Platzhalter wie `{0}` müssen erhalten bleiben. Fehlende Einträge werden automatisch auf Englisch angezeigt. Für Schriften mit eigenen Zeichen kann `_font` eine passende Windows-Schrift angeben.

Lokal erzeugte Dateien (nicht im Repository): `settings.json` (Auswahl und Optionen), `games.txt` (überwachte Prozesse), `watcher.log` (Protokoll).

## Hinweise

- Zurückgesetzt wird nur, was der Dienst selbst geändert hat. Wer während des Spiels HDR oder das Audiogerät von Hand umstellt, behält seine Einstellung.
- Das Umschalten des Audiogeräts nutzt die undokumentierte, aber seit Windows 7 stabile `IPolicyConfig`-Schnittstelle.
- Wieder gestartete Programme starten ohne ihre ursprünglichen Startparameter.

## Lizenz

[MIT](LICENSE) – frei nutzbar, veränderbar und weitergebbar, solange der Lizenzhinweis erhalten bleibt.
