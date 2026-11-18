# Patchnotes

## 2.0.0 – 18.11.2026

### Neu entwickelt

- Vollständige Neuentwicklung in Rust und Tauri statt C# und Avalonia. Eine .NET-Laufzeit wird nicht benötigt; Anwendungsdaten liegen in SQLite statt JSON/CSV.

### Neu

- Kalender mit Monats- und Jahresansicht sowie einstellbaren Farben.
- Jahresübersicht mit allen Anträgen und Status.
- Ent-Archivieren und Wiederherstellen gelöschter Anträge.
- Sicherung und Wiederherstellung mit automatischer Sicherheitskopie vor dem Wiederherstellen.
- Altdatenübernahme mit Vorschau, Abgleich je Jahr und speicherbarem Bericht.
- Diagnoseansicht mit exportierbarem Bericht.
- Hell-, Dunkel- und Systemmodus.
- Datumseingabe im Format TT.MM.JJJJ.
- Windows-Installation pro Benutzer ohne Administratorrechte.

### Geändert

- Landesweit gesetzliche Landesfeiertage zählen wieder als Feiertag, etwa Fronleichnam und Allerheiligen in NRW, Mariä Himmelfahrt im Saarland sowie Oster- und Pfingstsonntag in Brandenburg. In v1.3.2 zählten sie als Urlaubstag.
- „Optional“ heißt jetzt „regionale Feiertage“, etwa Mariä Himmelfahrt in Bayern.
- Archivieren und Löschen sind nur nach Ablehnung möglich; genehmigte Anträge zählen bis zur Ablehnung gegen den Anspruch.
- Schulferien werden per KMK-Kalenderdatei (ICS) importiert statt mitgeliefert.
- Feiertage werden offline berechnet, ohne Online-Abgleich.

### Entfernt

- Online-Feiertagsabgleich über Nager.Date.
- Eingebettete Schulferiendaten.

### Bekannte Einschränkungen

- Installer sind nicht signiert.
- Der Windows-Installer wurde per Cross-Build erzeugt und auf Windows bedient; ein nativer Build auf Windows ist noch nicht nachgewiesen.
- Das PDF enthält keine Tagesaufschlüsselung und keine Klasse, weil diese Felder in der Originalvorlage fehlen.
