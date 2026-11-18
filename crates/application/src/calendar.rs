use chrono::{DateTime, NaiveDate, Utc};

/// Ein Ferienzeitraum `[Beginn, Ende)` aus einem importierten Ferienkalender.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SchoolBreakRecord {
    pub start_date: NaiveDate,
    pub end_date_exclusive: NaiveDate,
    pub summary: String,
}

/// Importierte Schulferien eines Bundeslands mit Herkunft und Datenstand (REQ-HOL-003).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SchoolHolidayImport {
    pub state_code: String,
    pub source_url: String,
    /// Name der importierten Datei; kein Pfad, damit keine Ordnerstruktur gespeichert wird.
    pub file_name: String,
    pub imported_at_utc: DateTime<Utc>,
    /// Jüngster Änderungsstempel (DTSTAMP) der Quelle, falls vorhanden.
    pub latest_event_stamp_utc: Option<DateTime<Utc>>,
    pub content_sha256: String,
    pub breaks: Vec<SchoolBreakRecord>,
}
