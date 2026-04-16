# Urlaubstool

Plattformuebergreifende Avalonia-Desktop-App zur Erstellung von Urlaubsantraegen, Berechnung von Urlaubstagen, PDF-Export und Historienverwaltung. Die App verwendet dieselbe UI-Technologie wie RawImporter (Avalonia mit Fluent-Theme und eigenen Farbressourcen).

## Technische Grundlage
- RawImporter verwendet Avalonia (siehe Projektdatei und XAML-Ansichten).
- Urlaubstool nutzt denselben Ansatz mit Fluent-Theme und zentralen Farbressourcen in Urlaubstool.App/Colors.axaml.

## Template-Felder
Das PDF-Template in basis/Template.pdf enthaelt zwei identische Bereiche (Original und Kopie) mit folgenden Feldern:
- Personendaten: Name, Vorname, Adresse (Strasse/PLZ), Abteilung, Personalnummer
- Antragszeitraum: Startdatum, Enddatum
- Urlaubsberechnung: Gesamturlaub, bereits erhaltener Urlaub, mit diesem Antrag beantragt, Resturlaub, Anzahl Halbtage
- AZA-Tage: Mehrzeiliger Bereich fuer Berufsschultage im Zeitraum
- Unterschrift: Antragsdatum, Unterschriftsplatzhalter
- Verwaltung: Genehmigt, Bearbeitet, Personalabteilung, Ablehnungsgrund

## Projekte
- Urlaubstool.App: Avalonia-Oberflaeche (SetupWizard, Hauptfenster, Historie/Admin, Einstellungen)
- Urlaubstool.Domain: Berechnungslogik, Validierung, Modelle, Feiertags-Interfaces
- Urlaubstool.Infrastructure: Persistenz (Settings JSON, Ledger CSV), Offline-Feiertage, PDF-Export (iText7), Pfadservice
- Urlaubstool.Tests: xUnit-Tests mit FluentAssertions

## Build und Start
```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet run --project Urlaubstool.App
```

## Pfade fuer Einstellungen und Ledger
- macOS: ~/Library/Application Support/Urlaubstool/settings.json und ~/Library/Application Support/Urlaubstool/ledger.csv
- Windows: %APPDATA%/Urlaubstool/settings.json und entsprechendes ledger.csv
- PDF-Exporte: ~/Documents/Urlaubstool/Exports/ mit versionierten Dateinamen wie Urlaubsantrag_YYYY-MM-DD_bis_YYYY-MM-DD_vX.pdf

## Feiertagsdaten
- Gesetzliche Feiertage werden offline im Code berechnet (fixe Feiertage, Osterbezug, bundeslandspezifische Regeln inkl. Buss- und Bettag fuer SN).
- Schulferien liegen als eingebettete JSON-Datei vor: Urlaubstool.Infrastructure/Data/school_holidays.json.
- Verwendete Provider: PublicHolidayProvider und SchoolHolidayProvider (genutzt vom VacationCalculator).

## Berechnungsregeln (Domain)
- Nicht-Arbeitstage und gesetzliche Feiertage zaehlen 0 Urlaubstage.
- AZA-Tage (Arbeitszeitausgleich/Ueberstundenabbau) koennen im Zeitraum markiert werden und zaehlen ebenfalls 0 Urlaubstage.
- Schuelermodus: Schulferien haben Vorrang vor Berufsschulregeln; ganzer Berufsschultag fuehrt zu Fehler, halber Tag zaehlt maximal 0,5.
- Halbtage sind nur am Start- oder Endtag erlaubt, sonst Fehler.
- Jahresuebergreifende Zeitraeume sind nicht erlaubt.
- Unzureichender Resturlaub fuehrt zu einem harten Fehler.
- Es werden ausschliesslich DateOnly und decimal verwendet.

## Funktionen
- AZA-Tage-Auswahl im Hauptfenster per Checkbox und Datumsauswahl
- Plattformunterstuetzung: Windows (win-x64, win-arm64) und macOS (osx-arm64, osx-x64)
- PDF-Export mit iText7
- Historienverwaltung fuer Urlaubsantraege
- Persistente Einstellungen ueber Plattformgrenzen hinweg

## Persistenz
- Settings als JSON mit Schema-Version und sicherem Schreibvorgang (temporaere Datei, dann Replace)
- Ledger als CSV mit versionsfaehigem Schema, invariantem Dezimalformat und Retry bei Dateisperren

## PDF-Export-Architektur
Keine Excel-Abhaengigkeit, stattdessen reines PDF-Stempeln mit iText7.

### Template
- Eingebettete Ressource: Urlaubstool.Infrastructure/Pdf/Template.pdf
- A4-Einzelblatt mit zwei identischen Bereichen (Original oben, Kopie unten)

### Stempelservice
- PdfTemplateStampExportService laedt das eingebettete Template und schreibt Inhalte an feste Koordinaten
- Feldkoordinaten werden in TemplateLayout.TemplatePdf_v1.cs definiert
- Koordinatensystem: PDF-Ursprung unten links, Y als Baseline
- Texte werden auf MaxWidth begrenzt, bei Bedarf skaliert und mit Ellipse abgeschnitten
- Keine AcroForm-Felder erforderlich

### Feldlayout
- TemplateLayout.Original definiert die Koordinaten fuer den oberen Bereich
- TemplateLayout.Kopie spiegelt den oberen Bereich mit vertikalem Offset
- Felder definieren X, Y (Baseline), MaxWidth, FontSize und TextAlignment
- Mehrzeilige Felder (AZA-Tage) unterstuetzen Zeilenumbruch, LineHeight und MaxLines

### Platzhalter-Aufloesung
- PlaceholderResolver wandelt AppSettings, VacationRequest und CalculationResult in TemplateFieldValues um
- Deutsches Format fuer Datum (dd.MM.yyyy) und Dezimalzahlen (Komma)
- AZA-Tage werden als mehrzeiliger Text ausgegeben

### Export-Ablauf
1. Pflichtfelder pruefen (Name, Vorname, Adresse, Abteilung)
2. Platzhalterwerte mit deutschem Format erzeugen
3. Eingebettetes Template laden
4. Werte auf Original und Kopie stempeln
5. Versionierte PDF speichern
6. Eintrag im Ledger erfassen

## Template oder Layout aktualisieren
- Template austauschen: Urlaubstool.Infrastructure/Pdf/Template.pdf ersetzen und neu bauen
- Koordinaten anpassen: TemplateLayout.TemplatePdf_v1.cs bearbeiten
- Neue Felder einfuehren: TemplateFieldValues, PlaceholderResolver und Stamping-Logik erweitern

## Abgedeckte Tests
- VacationCalculator: Wochenenden, Feiertage, Schuelerregeln (voll/halb), Schulferien-Prioritaet, Halbtag-Regeln, Jahresgrenze, Resturlaub, bekannte Feiertage
- Persistenz: Roundtrip-Tests fuer Settings und Ledger inkl. Dezimalwerte und Auswahlen

## Laufzeit-Hinweise
- UI-Texte sind Deutsch, Code-Kommentare teilweise Englisch aus Wartbarkeitsgruenden
- App laeuft vollstaendig offline
- PDF-Export benoetigt keine externen Programme

============================================================
11) AUSFUEHRUNG, DESK-CHECKS UND STABILITAETSSCHLEIFEN (PFLICHT)
============================================================

11.1 Anwendung starten
- App aus dem Quellcode starten:
  - dotnet run --project Urlaubstool.App
- Die App muss ohne Absturz bis ins Hauptfenster starten.

11.2 Wenn die App nicht startet
Bei Startfehlern (Absturz, Exception, Build-Fehler, fehlende Ressource, Laufzeitfehler):
- Exakte Ursache ermitteln ueber Build-Ausgabe, Stacktrace und Logs.
- Ursache dauerhaft in Code, Konfiguration oder Ressourcen beheben.
- Danach erneut ausfuehren:
  - dotnet build -c Release
  - dotnet test -c Release
  - dotnet run --project Urlaubstool.App
- Wiederholen, bis der Start stabil funktioniert.

11.3 Desk-Checks und Simulationen (mindestens 5 saubere Durchlaeufe je Szenario)
Nach erfolgreichem Start sind alle Szenarien Ende-zu-Ende zu testen. Erforderlich sind pro Lauf:
- keine Abstuerze
- keine unbehandelten Exceptions
- keine Warnungen aufgrund fehlerhafter Logik
- keine falschen Berechnungen
- keine fehlerhaften UI-Zustaende

Jedes Szenario muss mindestens 5-mal in Folge fehlerfrei laufen. Bei einem Fehler beginnt der Zaehler fuer dieses Szenario erneut.

Szenario A - Basis-Modus ohne Schuelerparameter
1. Schuelerparameter deaktivieren.
2. Arbeitstage Mo-Fr, Anspruch 30 Tage setzen.
3. Normale Woche ohne Feiertage beantragen.
4. Summe 5 Tage pruefen.
5. PDF-Export pruefen.
6. Historieneintrag pruefen.

Szenario B - Feiertagsausschluss
1. Zeitraum mit bekanntem bundesweiten Feiertag waehlen (z. B. 03.10).
2. Feiertag muss mit 0 zaehlen.
3. PDF und Summen pruefen.

Szenario C - Schuelermodus, voller Schultag blockiert
1. Schuelerparameter aktivieren und mindestens einen Wochentag auf Voll setzen.
2. Zeitraum mit diesem Wochentag ausserhalb der Schulferien waehlen.
3. Harte Fehlermeldung und blockierter Export muessen auftreten.
4. Fehlermeldung muss klar und auf Deutsch sein.

Szenario D - Schuelermodus, halber Schultag
1. Mindestens einen Wochentag auf Halb setzen.
2. Zeitraum mit diesem Wochentag ausserhalb der Schulferien waehlen.
3. Tag darf maximal 0,5 zaehlen und Summe muss stimmen.

Szenario E - Halbtag nur am Rand
1. Mehrtaegigen Zeitraum erstellen.
2. StartHalfDay und/oder EndHalfDay setzen.
3. Nur Randtage duerfen reduziert sein.
4. Ungueltige Halbtag-Konfiguration muss harten Fehler ausloesen.

Szenario F - Jahresgrenzenfehler
1. Zeitraum ueber 31.12 auf 01.01 erstellen.
2. Harten Fehler und blockierten Export pruefen.

Szenario G - Historienablauf
1. Antrag erstellen/exportieren.
2. In der Historie genehmigen.
3. Resturlaub muss korrekt sinken.
4. Einen anderen Antrag ablehnen und korrekte Rueckbuchung pruefen.

11.4 Nach jeder Korrektur immer Build, Test und Run erneut ausfuehren
Nach jedem Fix in diesen Schleifen ist Pflicht:
- dotnet build -c Release
- dotnet test -c Release
- dotnet run --project Urlaubstool.App

11.5 Abbruchbedingung (erfolgreich)
Die Arbeit ist erst abgeschlossen, wenn:
- die App stabil startet,
- alle Szenarien A-G jeweils 5 saubere Durchlaeufe erreicht haben,
- dotnet test -c Release komplett grün ist.

Keine Abkuerzungen, keine offenen TODOs und keine unbelegten Annahmen.
