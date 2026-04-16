# Urlaubstool

Urlaubstool ist eine Desktop-Anwendung zur Planung und Dokumentation von Urlaubsanträgen.
Die App berechnet Urlaubstage automatisch, berücksichtigt Feiertage sowie optionale Berufsschulregeln und erzeugt druckfertige PDF-Anträge.

## Für wen ist die App gedacht?
- Mitarbeitende mit klassischer Urlaubsplanung
- Auszubildende mit Berufsschul- und Ferienlogik
- Teams, die Urlaubsanträge lokal und ohne Cloud speichern möchten

## Hauptfunktionen
- Urlaub von Start- bis Enddatum über Kalender auswählen
- AZA-Tage (Arbeitszeitausgleich) als 0-Tage markieren
- Halbtag-Regeln am Start- und Enddatum berücksichtigen
- Automatische Berechnung von beantragt, verbraucht und Resturlaub
- PDF-Export des Urlaubsantrags
- Historie mit Status (z. B. genehmigt, abgelehnt, archiviert)
- Frei konfigurierbare Kalenderfarben per HEX-Code in den Einstellungen

## Auslieferung als Executable
Die Anwendung ist für die direkte Nutzung als fertige Executable/App ausgelegt.
Es ist keine eigene Entwicklungsumgebung erforderlich.

## Systemunterstützung
- Windows (x64, ARM64)
- macOS (Apple Silicon)

## Erste Schritte
1. App starten.
2. Beim ersten Start die Einstellungen ausfüllen (Name, Jahresurlaub, Arbeitstage usw.).
3. Zeitraum im Kalender wählen.
4. Optional Halbtag oder AZA-Tage setzen.
5. Ergebnis prüfen und PDF exportieren.

## Speicherorte der App-Daten
- macOS
  - Einstellungen: ~/Library/Application Support/Urlaubstool/settings.json
  - Historie/Ledger: ~/Library/Application Support/Urlaubstool/ledger.csv
- Windows
  - Einstellungen: %APPDATA%/Urlaubstool/settings.json
  - Historie/Ledger: %APPDATA%/Urlaubstool/ledger.csv
- PDF-Exporte
  - Standardordner: ~/Documents/Urlaubstool/Exports/

## Wichtige Regeln auf einen Blick
- Wochenenden und gesetzliche Feiertage zählen nicht als Urlaubstage.
- Ganztags-Berufsschultage können Urlaub blockieren.
- Halbe Berufsschultage werden mit maximal 0,5 Urlaubstag berechnet.
- Jahresübergreifende Urlaubszeiträume sind nicht erlaubt.

## Datenschutz und Offline-Betrieb
- Die App arbeitet lokal und kann ohne Internet betrieben werden.
- Es werden keine Cloud-Konten für den Normalbetrieb benötigt.

## Support
Bei Problemen bitte mit kurzer Fehlerbeschreibung, Plattform (Windows/macOS) und App-Version ein issue im Repository erstellen.
