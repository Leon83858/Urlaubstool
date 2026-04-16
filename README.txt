Urlaubstool — Bedienungsanleitung & Technische Übersicht

Überblick
--------
Dieses Projekt ist ein kleines Desktop-Tool zum Erstellen und Verwalten von Urlaubsanträgen. Es unterstützt:
- Berechnung der anzurechnenden Urlaubstage (inkl. halber Tage)
- Berücksichtigung von AZA-Tagen (Arbeitszeitausgleich) — wählbar per Checkbox
- Unterstützung für Schüler/Studenten mit Schulferien- und Berufsschul-Tagen
- Export des Antrags als PDF (AcroForm-Füllung)
- Historie aller Anträge (JSONL-Event-Sourcing) mit Möglichkeit zu genehmigen, ablehnen, archivieren und löschen

Wichtige Pfade
--------------
- App-Daten (Basis):
  - macOS/Windows: `~/Documents/Urlaubstool/`
  - Backward Compatibility: Falls vorhanden, wird `~/Documents/Urlaubstool` verwendet (auch bei Windows: `C:\Users\<User>\Documents\Urlaubstool\`)
  - Neue Standard-Location (falls keine alte Version existiert):
    - macOS: `~/Library/Application Support/Urlaubstool`
    - Windows: `%APPDATA%\Urlaubstool`
- Settings (Benutzerkonfiguration):
  - `~/Documents/Urlaubstool/Settings/settings.json`
- History (JSONL-Event-Sourcing):
  - `~/Documents/Urlaubstool/History/history.jsonl`
  - Bad History (nicht parsbare Events): `~/Documents/Urlaubstool/History/history.bad.jsonl`
- Exportierte PDFs:
  - `~/Documents/Urlaubstool/Exports/` (konfigurierbar in Settings)

Kurzanleitung — Bediener
------------------------
- Hinweis: Beim ersten Start erscheint ein Setup-Fenster, in dem du personenbezogene Daten (Name, Vorname, Abteilung, Jahresurlaub usw.) eintragen musst. Diese Angaben werden für die PDF-Generierung und die Historie benötigt.

1. Starten der App (ausgeliefert):
  - macOS: Doppelklicke die erzeugte `Urlaubstool.app` (z. B. auf dem Desktop).
  - Windows: Doppelklicke die `Urlaubstool.exe` im Release-Ordner (z. B. `C:\Users\<Benutzer>\Desktop\Urlaubstool_Windows\Urlaubstool.exe`).

  Hinweis: Bei macOS zeigt Gatekeeper beim ersten Start eventuell eine Sicherheitsabfrage. Verwende Rechtsklick → "Öffnen", falls die App blockiert wird.

2. Zeitraum wählen:
  - Wähle `Von` und `Bis` mit dem DatePicker.
  - Falls der erste oder letzte Tag ein halber Tag ist, aktiviere `Halber Tag (Start)` bzw. `Halber Tag (Ende)`.

3. AZA-Tage (Arbeitszeitausgleich):
   - Aktiviere die Checkbox „Im Urlaubszeitraum sind AZA-Tage“, um die UI zum Hinzufügen von AZA-Daten freizuschalten.
   - Füge einzelne AZA-Tage hinzu. Diese Tage werden beim Berechnen NICHT als Urlaubstage gezählt.
   - Wichtig: Wenn die Checkbox deaktiviert ist, werden vorhandene AZA-Datumseinträge nicht berücksichtigt (auch wenn Werte im UI vorhanden sind).

4. Schüler/Studenten-Modus:
   - In den Einstellungen (`Einstellungen`-Fenster) kannst du den Student-Modus aktivieren und das Bundesland wählen.
   - Wenn Student-Modus aktiv ist, zieht die App Schulferien (offline-cache) und Berufsschultage in die Berechnung ein.

5. PDF-Export:
   - Klicke `PDF exportieren` um ein ausgefülltes PDF zu erzeugen.
   - Das erzeugte PDF wird im Export-Ordner abgelegt oder im in den Einstellungen konfigurierten Exportpfad.
   - Beim erfolgreichen Export wird der History-Eintrag mit einem `Exported`-Event versehen und das `PdfPath` gespeichert.

6. Historie:
   - Die Historie zeigt alle gespeicherten Anträge für das gewählte Jahr an.
   - Aktionen pro Eintrag: PDF öffnen, Genehmigen, Archivieren, Löschen.
   - Beim Ablehnen oder Löschen eines Antrags versucht die App, das zugehörige PDF aus dem Export-Ordner zu entfernen. Fehlschläge werden protokolliert, aber das Event wird trotzdem gespeichert.

Technische Details / Architektur
--------------------------------
- Sprache & UI
  - C#  (Target: .NET 10 / net10.0)
  - UI: Avalonia
- Projekte
  - `Urlaubstool.App` — UI / ViewModels / Fenster
  - `Urlaubstool.Domain` — Domänenmodelle, `VacationCalculator` (Kernlogik)
  - `Urlaubstool.Infrastructure` — PDF-Export, History-Store, Pfad-/Settings-Services, Holiday-Providers
  - Testprojekte: `Urlaubstool.Tests`, `Urlaubstool.EndToEndTests`, `Urlaubstool.DeskCheckTests`
- Berechnung
  - `VacationCalculator.Calculate(VacationRequest)` führt die Tagesauswertung durch.
  - Regeln (Kurzfassung):
    - Wochenend-/Nicht-Arbeitstag => kein Tag
    - Feiertag => kein Tag
    - AZA-Tag => 0 Tage (wenn aktiv)
    - Schulferien (Student-Modus) => keine Abzugstage
    - Berufsschule (ganztags/halb) => Einfluss auf Fehlermeldungen / Tageswert
- History/Event-Store
  - Events werden in JSONL (eine Zeile pro Event) persistiert. Projektion baut `HistoryEntry` aus den Events.
  - Wichtige Events: Created, Exported (speichert PdfPath), Approved, Rejected, Archived, Deleted.
  - Löschen/Ablehnen: Implementierung löscht zuvor vorhandene PDF-Datei (sofern bekannt) und hängt dann das Event an.

Einstellungen
------------
- Öffne `Einstellungen` über das Menü.
- Felder:
  - Name / Vorname / Abteilung — für PDF-Placeholder
  - Jahresurlaub — jährlicher Urlaubsanspruch
  - Workdays — welche Wochentage als Arbeitstage gelten
  - StudentActive — Student/Schüler-Modus (true/false)
  - Bundesland — für Schulferienberechnung
  - ExportPath — optionaler Pfad für PDF-Export (falls leer: Standardordner wird benutzt)

Build & Release Hinweise
------------------------
- Entwicklung / Run (Debug):
  ```bash
  cd src
  dotnet build -c Debug
  dotnet run --project Urlaubstool.App/Urlaubstool.App.csproj -c Debug
  ```

- Release / macOS Bundle: Es gibt Hilfs-Skripte im Repo:
  - `assemble_mac_app.sh` — macOS packaging helper (prüfe Skriptoptionen bevor du es ausführst)
  - `build_release.sh` — Release-Build-Skript
  - `generate_icns.sh` / `convert_ico.py` — Icon-Generierung

  Beispiele:

  - Voller Release-Build (Windows + macOS) und Paketierung (erzeugt Ordner auf dem Desktop):

    ```bash
    cd src
    ./build_release.sh
    ```

    Ergebnis:
    - Windows Build (self-contained single-file) wird in `~/Desktop/Urlaubstool_Windows/` abgelegt (enthält `Urlaubstool.exe`).
    - macOS Build temporär in `~/Desktop/Urlaubstool_Mac_Temp/`, das Skript erstellt anschließend `~/Desktop/Urlaubstool.app`.

  - Alternativ einzelnes Packaging für macOS (falls bereits in `Urlaubstool_Mac_Temp`):

    ```bash
    cd src
    ./assemble_mac_app.sh
    ```

    Das Skript erzeugt `~/Desktop/Urlaubstool.app` und kopiert notwendige Ressourcen.

Hinweise zur Ausführung auf macOS & Windows
---------------------------------------------
- macOS: Starte die App über das Bundle (`Urlaubstool.app`), Doppelklick auf die Datei.
- Windows: Doppelklick auf `Urlaubstool.exe`.
- macOS kann beim ersten Start eine Sicherheitsabfrage zeigen (Gatekeeper). Falls das App-Icon nicht geöffnet wird, öffne das Kontextmenü (Rechtsklick) und wähle `Öffnen`.
- Die App nutzt automatisch die richtigen Pfade für Windows und macOS:
  - Daten speichern in `~/Documents/Urlaubstool/` (plattformübergreifend)
  - Settings: `~/Documents/Urlaubstool/Settings/settings.json`
  - History: `~/Documents/Urlaubstool/History/history.jsonl`
  - Exports: `~/Documents/Urlaubstool/Exports/`

Fehlerbehebung & Troubleshooting
--------------------------------
- App lässt sich nicht starten / GUI erscheint nicht:
  - Prüfe `dotnet --info` und ob `dotnet` korrekt installiert ist.
  - Stelle sicher, dass alle Abhängigkeiten wiederhergestellt wurden: `dotnet restore`.
  - Prüfe Console-Output beim `dotnet run` auf Exceptions.

- PDF wird nicht erzeugt:
  - Prüfe, ob die Template-Datei vorhanden ist (wird von der PDF-Export-Logik geladen).
  - Prüfe Schreibrechte für den Export-Ordner.

- PDF wird nicht gelöscht beim Ablehnen/Löschen:
  - Die App versucht, die Datei zu löschen; falls Berechtigungen fehlen oder die Datei bereits entfernt wurde, wird eine Warnung geloggt, das Event wird trotzdem gespeichert.

Sicherheit & Datenschutz
------------------------
- Exportierte PDFs können personenbezogene Daten enthalten (Name, Abteilung, Termine). Achte auf sichere Aufbewahrung und bereinige Export-Ordner bei Bedarf.
- Die History-Datei (`history.jsonl`) enthält event-sourcing Daten; entferne diese Dateien nur, wenn du dir über die Konsequenzen bewusst bist.

Entwicklung & Tests
-------------------
- Unit- und Integrationstests liegen in den Testprojekten. Tests mit:

  ```bash
  cd src
  dotnet test
  ```

- Wenn du Änderungen an der Berechnungslogik machst, erweitere `VacationCalculatorTests` in `Urlaubstool.Tests`.

Was noch zu beachten ist / bekannte Warnungen
-------------------------------------------
- Beim Build können NuGet-Warnungen auftreten (z. B. Versionen von `QuestPDF`, oder Sicherheits-Alerts für `BouncyCastle.Cryptography`). Diese sollten geprüft und—falls nötig—abhängigkeitsseitig aktualisiert werden.

Kontribution
------------
- Fork -> Feature-Branch -> Pull Request.
- Bitte Unit-Tests für Änderungen an Geschäftslogik hinzufügen.

Datei
-----
Diese Anleitung wurde als `src/README.txt` erstellt.

---
Falls du möchtest, erstelle ich zusätzlich eine `README.md` mit gleichen Inhalten, oder passe den Text an ein kurzes, druckbares Benutzerhandbuch an.