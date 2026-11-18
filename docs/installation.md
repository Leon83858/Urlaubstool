# Installation, Datenübernahme, Sicherung und Rückweg

Gilt für Version 2.0.0. Die Installer liegen im Projektordner unter `Installer/` (mit `SHA256SUMS.txt`). Beide sind nicht mit einem Entwicklerzertifikat signiert. Windows ist gebaut, aber noch nicht auf einem Windows-Rechner geprüft.

## Installation (macOS)

1. `Urlaubstool_2.0.0_arm64.dmg` öffnen und „Urlaubstool“ in „Programme“ ziehen.
2. Prüfsumme vergleichen (optional): `shasum -a 256 Urlaubstool_2.0.0_arm64.dmg` muss mit `SHA256SUMS.txt` übereinstimmen.
3. Erster Start: Die App ist nicht signiert. Im Finder Rechtsklick auf „Urlaubstool“ → „Öffnen“ → „Öffnen“ bestätigen (oder Systemeinstellungen → Datenschutz & Sicherheit → „Dennoch öffnen“).
4. Es wird keine .NET-Laufzeit benötigt. Die App arbeitet vollständig lokal und stellt keine Netzwerkverbindungen her.

## Installation (Windows 10/11, x64)

1. `Urlaubstool_2.0.0_x64-setup.exe` ausführen. Installiert wird nur für den angemeldeten Benutzer; Administratorrechte sind nicht nötig.
2. Prüfsumme (optional, PowerShell): `Get-FileHash Urlaubstool_2.0.0_x64-setup.exe` muss mit `SHA256SUMS.txt` übereinstimmen.
3. Erster Start: Weil das Setup nicht signiert ist, warnt Windows SmartScreen. „Weitere Informationen“ → „Trotzdem ausführen“.
4. Fehlt die WebView2-Laufzeit (auf Windows 10 selten), lädt das Setup sie von Microsoft nach; dafür ist einmalig Internet nötig.
5. Daten liegen unter `%APPDATA%\de.leonpilger.urlaubstool\`, das Protokoll unter `%LOCALAPPDATA%\de.leonpilger.urlaubstool\logs\`; den genauen Ort zeigt „Diagnose“.

## Wo die Daten liegen

| Inhalt | Ort (macOS) |
|---|---|
| Datenbank | `~/Library/Application Support/de.leonpilger.urlaubstool/urlaubstool.sqlite` |
| Automatische Sicherungen (vor Wiederherstellung/Migration) | `~/Library/Application Support/de.leonpilger.urlaubstool/Sicherungen/` |
| Diagnoseprotokoll | `~/Library/Logs/de.leonpilger.urlaubstool/urlaubstool.log` |

Den genauen Ort zeigt die App unter „Diagnose“.

## Daten aus der bisherigen Anwendung übernehmen

1. Vorher die Altdaten-Ordner der bisherigen Anwendung kopieren (zusätzliche Sicherheit; die App verändert die Originale ohnehin nicht).
2. Einstellungen → „Altdaten“ → „Altdatei auswählen …“. Empfohlene Reihenfolge: `settings.json`, dann `ledger.csv` **oder** die Verlaufsdatei (`history.jsonl`/`history.csv`) desselben Datenbestands.
3. Die Vorschau prüfen: Anzahl gelesen/bereit/übersprungen/fehlerhaft und die Hinweise je Datensatz. Bei fehlerhaften Datensätzen ist der Import gesperrt.
4. „Importieren“. Vorher werden die Altdatei und die aktuelle Datenbank im Ordner `Sicherungen` gesichert. Ein erneuter Import derselben Datei legt nichts doppelt an.
5. Danach Übersicht, Historie und Kalender stichprobenartig mit der bisherigen Anwendung vergleichen (Anzahl Anträge, genehmigte Tage je Jahr).

## Sicherung und Wiederherstellung

- Einstellungen → „Sicherung erstellen“ schreibt eine `.sqlite`-Datei an einen frei wählbaren Ort. PDF-Dateien sind nicht enthalten.
- „Sicherung wiederherstellen“ ersetzt alle Daten durch die gewählte Sicherung. Der Stand davor wird automatisch als `Sicherungen/vor-Wiederherstellung_<Zeit>.sqlite` abgelegt.

## Update

1. In der laufenden App „Sicherung erstellen“.
2. Neue Version wie oben installieren (vorhandene App ersetzen). Das Datenbankschema wird beim Start automatisch aktualisiert.

## Rückweg (Rollback)

1. Neue Version beenden, vorherige Version installieren.
2. Hat die neue Version das Datenbankschema erhöht, verweigert die ältere Version das Öffnen der Datenbank mit der Meldung „Das Datenbankschema … ist neuer als unterstützt“; es wird dabei nichts verändert. Dann in der älteren Version „Sicherung wiederherstellen“ mit der vor dem Update erstellten Sicherung bzw. der Datei aus `Sicherungen/vor-Migration_…`/`vor-Wiederherstellung_…` wählen.
3. Notfall ohne App: App beenden und `urlaubstool.sqlite` durch eine Sicherungsdatei ersetzen (vorher die aktuelle Datei umbenennen, nicht löschen).

## Deinstallation

App aus „Programme“ löschen. Die Daten bleiben in den oben genannten Ordnern, bis sie manuell entfernt werden.
