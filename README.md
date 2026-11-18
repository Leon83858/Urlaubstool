# Urlaubstool 2.0

Urlaubstool ist eine lokale Desktop-App für Urlaubsplanung und Urlaubsanträge. Sie eignet sich auch für Auszubildende mit Berufsschule und läuft auf macOS und Windows.

## Funktionen

- Urlaubsberechnung in halben Tagen mit konfigurierbarer Arbeitswoche und offline berechneten gesetzlichen Feiertagen aller 16 Bundesländer; regionale Feiertage lassen sich optional berücksichtigen.
- AZA-Tage, Halbtage, Berufsschulmodus und Schulferienimport aus einer KMK-ICS-Datei.
- Antragsstatus: Entwurf → Exportiert → Genehmigt/Abgelehnt → Archiviert/Gelöscht; gelöschte Anträge lassen sich wiederherstellen und archivierte ent-archivieren.
- PDF-Antrag mit der Originalvorlage.
- Jahresübersicht mit Antragsliste und Status, Kalender in Monats- und Jahresansicht sowie Historie.
- Datenbanksicherung und Wiederherstellung.
- Übernahme von Version 1.x mit Vorschau und Abgleich.
- Diagnosebericht ohne personenbezogene Daten sowie Hell-, Dunkel- und Systemmodus.

## Download und Installation

Die Installer und Prüfsummen werden im [GitHub-Release v2.0.0](https://github.com/Leon83858/Urlaubstool/releases/tag/v2.0.0) bereitgestellt.

- **macOS 11 oder neuer (Apple Silicon):** DMG öffnen und Urlaubstool in „Programme“ ziehen. Beim ersten Start im Finder rechtsklicken, „Öffnen“ wählen und nochmals bestätigen.
- **Windows 10/11 x64:** Setup starten und bei der SmartScreen-Warnung „Weitere Informationen“ → „Trotzdem ausführen“ wählen. Die Installation erfolgt für den angemeldeten Benutzer.

Beide Installer sind nicht mit einem Entwicklerzertifikat signiert. Deshalb fragt macOS beim ersten Start nach; Windows SmartScreen zeigt eine Warnung. Details und Prüfsummenbefehle stehen in [docs/installation.md](docs/installation.md).

## Datenschutz und Speicherorte

Anträge und Einstellungen bleiben lokal in einer SQLite-Datenbank. Die App stellt keine Netzwerkverbindungen her und sendet keine Telemetrie. Eine Ausnahme außerhalb des App-Betriebs: Falls unter Windows WebView2 fehlt, kann das Setup dessen Laufzeit einmalig von Microsoft herunterladen.

- **macOS:** Datenbank unter `~/Library/Application Support/de.leonpilger.urlaubstool/`, Protokoll unter `~/Library/Logs/de.leonpilger.urlaubstool/`.
- **Windows:** Datenbank unter `%APPDATA%\de.leonpilger.urlaubstool\`, Protokoll unter `%LOCALAPPDATA%\de.leonpilger.urlaubstool\logs\`.

Den genauen Ort zeigt die Diagnoseansicht.

## Umstieg von Version 1.x

In **Einstellungen → Altdaten** lassen sich `settings.json`, `ledger.csv`, `history.csv` und `history.jsonl` übernehmen. Die Originaldateien bleiben unverändert; vor dem Import werden die Altdatei und die aktuelle Datenbank gesichert. Die alte Version bleibt unter dem Tag [`v1.3.2`](https://github.com/Leon83858/Urlaubstool/tree/v1.3.2) und im Branch `legacy/v1` erhalten.

## Selbst bauen

Voraussetzungen: Rust 1.90 oder neuer, Node.js 22 und die [Tauri-2-Voraussetzungen](https://v2.tauri.app/start/prerequisites/) für das jeweilige Betriebssystem.

```sh
npm ci
npm run tauri dev
npm run installer
```

Der Windows-Installer lässt sich auf dem Mac per Cross-Build mit `scripts/bundle-windows.sh` bauen. Das Skript installiert bei Bedarf nsis, llvm, lld und cargo-xwin.

## Projektaufbau

| Pfad | Zweck |
|---|---|
| `crates/domain` | Urlaubsregeln und Domainmodelle |
| `crates/application` | Anwendungsfälle und PDF-Vorlage |
| `crates/api` | Anwendungs-API |
| `crates/holidays` | Gesetzliche Feiertage und ICS-Ferienimport |
| `crates/migration` | Übernahme von Altdaten |
| `crates/storage` | Lokale SQLite-Speicherung |
| `src-tauri` | Tauri-Anwendung und Systemintegration |
| `src` | React-/TypeScript-Oberfläche |
| `assets`, `scripts`, `docs` | App-Symbol, Buildskripte und Installationsanleitung |

## Lizenz

GPL-3.0-only (siehe [LICENSE](LICENSE)).
