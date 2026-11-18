use std::fs;
use std::path::{Path, PathBuf};

use chrono::{DateTime, NaiveDate};
use csv::{ReaderBuilder, StringRecord};
use serde_json::Value;
use sha2::{Digest, Sha256};
use thiserror::Error;

const LEDGER_HEADER: [&str; 13] = [
    "SchemaVersion",
    "Year",
    "RequestId",
    "CreatedAt",
    "StartDate",
    "EndDate",
    "StartHalfDay",
    "EndHalfDay",
    "DaysRequested",
    "Status",
    "RejectionReason",
    "ArchivedAt",
    "PdfPath",
];

const HISTORY_HEADER: [&str; 12] = [
    "EventId",
    "RequestId",
    "Timestamp",
    "EventType",
    "Year",
    "StartDate",
    "EndDate",
    "StartHalfDay",
    "EndHalfDay",
    "CalculatedDays",
    "PdfPath",
    "RejectionReason",
];

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum LegacyFormat {
    SettingsJson { schema_version: u32 },
    LedgerCsv { schema_version: u32 },
    HistoryCsv,
    HistoryJsonl,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum IssueKind {
    InvalidEncoding,
    InvalidHeader,
    InvalidRecord,
    UnsupportedVersion,
    UnsupportedStatus,
    UnknownEventType,
    InvalidJson,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DatasetIssue {
    pub record_number: usize,
    pub kind: IssueKind,
    pub message: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MigrationPreview {
    pub source_path: PathBuf,
    pub source_sha256: String,
    pub format: LegacyFormat,
    pub records_read: usize,
    pub records_ready: usize,
    pub records_skipped: usize,
    pub records_failed: usize,
    pub issues: Vec<DatasetIssue>,
}

impl MigrationPreview {
    pub fn can_import(&self) -> bool {
        self.records_failed == 0 && self.records_ready > 0
    }
}

#[derive(Debug, Error)]
pub enum PreviewError {
    #[error("Das Dateiformat wird nicht unterstützt: {0}")]
    UnsupportedFormat(PathBuf),
    #[error("Die Datei konnte nicht gelesen werden: {path}: {source}")]
    Io {
        path: PathBuf,
        #[source]
        source: std::io::Error,
    },
    #[error("Die Datei ist kein gültiges UTF-8/UTF-8-BOM-Dokument: {0}")]
    InvalidEncoding(PathBuf),
}

pub fn preview_file(path: impl AsRef<Path>) -> Result<MigrationPreview, PreviewError> {
    let path = path.as_ref();
    let bytes = fs::read(path).map_err(|source| PreviewError::Io {
        path: path.to_path_buf(),
        source,
    })?;
    let mut hasher = Sha256::new();
    hasher.update(&bytes);
    let source_sha256 = hasher
        .finalize()
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect();
    let content = decode_utf8(&bytes).ok_or_else(|| PreviewError::InvalidEncoding(path.into()))?;
    let file_name = path
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or_default()
        .to_ascii_lowercase();

    let mut preview = match file_name.as_str() {
        "settings.json" => preview_settings(path, content)?,
        "ledger.csv" => preview_ledger(path, content)?,
        "history.csv" => preview_history_csv(path, content)?,
        "history.jsonl" => preview_history_jsonl(path, content),
        _ => return Err(PreviewError::UnsupportedFormat(path.to_path_buf())),
    };
    preview.source_sha256 = source_sha256;
    Ok(preview)
}

fn decode_utf8(bytes: &[u8]) -> Option<&str> {
    let bytes = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]).unwrap_or(bytes);
    std::str::from_utf8(bytes).ok()
}

fn preview_settings(path: &Path, content: &str) -> Result<MigrationPreview, PreviewError> {
    let value: Value = match serde_json::from_str(content) {
        Ok(value) => value,
        Err(error) => {
            return Ok(failed_preview(
                path,
                LegacyFormat::SettingsJson { schema_version: 0 },
                1,
                IssueKind::InvalidJson,
                error.to_string(),
            ));
        }
    };
    let Some(object) = value.as_object() else {
        return Ok(failed_preview(
            path,
            LegacyFormat::SettingsJson { schema_version: 0 },
            1,
            IssueKind::InvalidRecord,
            "Die Einstellungsdatei muss ein JSON-Objekt enthalten.".into(),
        ));
    };
    let version = object
        .get("Version")
        .or_else(|| object.get("version"))
        .and_then(Value::as_u64)
        .map(|version| version as u32)
        .unwrap_or_else(|| u32::from(object.contains_key("Name")));
    let format = LegacyFormat::SettingsJson {
        schema_version: version,
    };
    if !matches!(version, 1 | 2) {
        return Ok(failed_preview(
            path,
            format,
            1,
            IssueKind::UnsupportedVersion,
            format!("Settings-Schema {version} wird nicht unterstützt."),
        ));
    }
    let has_name = object.contains_key("Name")
        || object.contains_key("Vorname")
        || object.contains_key("Nachname");
    if !has_name {
        return Ok(failed_preview(
            path,
            format,
            1,
            IssueKind::InvalidRecord,
            "Es fehlen Name/Vorname/Nachname in den Einstellungen.".into(),
        ));
    }
    Ok(MigrationPreview {
        source_path: path.to_path_buf(),
        source_sha256: String::new(),
        format,
        records_read: 1,
        records_ready: 1,
        records_skipped: 0,
        records_failed: 0,
        issues: Vec::new(),
    })
}

fn preview_ledger(path: &Path, content: &str) -> Result<MigrationPreview, PreviewError> {
    let mut reader = ReaderBuilder::new()
        .has_headers(true)
        .flexible(true)
        .from_reader(content.as_bytes());
    let headers = match reader.headers() {
        Ok(headers) => headers.clone(),
        Err(error) => {
            return Ok(failed_preview(
                path,
                LegacyFormat::LedgerCsv { schema_version: 0 },
                0,
                IssueKind::InvalidHeader,
                error.to_string(),
            ));
        }
    };
    if !header_matches(&headers, &LEDGER_HEADER) {
        return Ok(failed_preview(
            path,
            LegacyFormat::LedgerCsv { schema_version: 0 },
            0,
            IssueKind::InvalidHeader,
            "Der Ledger-Header entspricht nicht dem belegten 13-Spalten-Schema.".into(),
        ));
    }

    let mut preview = base_preview(path, LegacyFormat::LedgerCsv { schema_version: 1 });
    for (index, record) in reader.records().enumerate() {
        let record_number = index + 2;
        preview.records_read += 1;
        match record {
            Ok(record) => match validate_ledger_record(&record) {
                Ok(()) => preview.records_ready += 1,
                Err((kind, message)) => {
                    preview.records_failed += 1;
                    preview.issues.push(DatasetIssue {
                        record_number,
                        kind,
                        message,
                    });
                }
            },
            Err(error) => {
                preview.records_failed += 1;
                preview.issues.push(DatasetIssue {
                    record_number,
                    kind: IssueKind::InvalidRecord,
                    message: error.to_string(),
                });
            }
        }
    }
    Ok(preview)
}

fn preview_history_csv(path: &Path, content: &str) -> Result<MigrationPreview, PreviewError> {
    let mut reader = ReaderBuilder::new()
        .has_headers(true)
        .flexible(true)
        .from_reader(content.as_bytes());
    let headers = match reader.headers() {
        Ok(headers) => headers.clone(),
        Err(error) => {
            return Ok(failed_preview(
                path,
                LegacyFormat::HistoryCsv,
                0,
                IssueKind::InvalidHeader,
                error.to_string(),
            ));
        }
    };
    if !header_matches(&headers, &HISTORY_HEADER) {
        return Ok(failed_preview(
            path,
            LegacyFormat::HistoryCsv,
            0,
            IssueKind::InvalidHeader,
            "Der History-Header entspricht nicht dem belegten 12-Spalten-Schema.".into(),
        ));
    }

    let mut preview = base_preview(path, LegacyFormat::HistoryCsv);
    for (index, record) in reader.records().enumerate() {
        let record_number = index + 2;
        preview.records_read += 1;
        match record {
            Ok(record) => match validate_history_record(&record) {
                Ok(RecordDisposition::Ready) => preview.records_ready += 1,
                Ok(RecordDisposition::Skipped) => preview.records_skipped += 1,
                Err((kind, message)) => {
                    preview.records_failed += 1;
                    preview.issues.push(DatasetIssue {
                        record_number,
                        kind,
                        message,
                    });
                }
            },
            Err(error) => {
                preview.records_failed += 1;
                preview.issues.push(DatasetIssue {
                    record_number,
                    kind: IssueKind::InvalidRecord,
                    message: error.to_string(),
                });
            }
        }
    }
    Ok(preview)
}

fn preview_history_jsonl(path: &Path, content: &str) -> MigrationPreview {
    let mut preview = base_preview(path, LegacyFormat::HistoryJsonl);
    for (index, line) in content.lines().enumerate() {
        preview.records_read += 1;
        let record_number = index + 1;
        if line.trim().is_empty() {
            preview.records_skipped += 1;
            continue;
        }
        match serde_json::from_str::<Value>(line) {
            Ok(value) => match validate_history_event_json(&value) {
                Ok(RecordDisposition::Ready) => preview.records_ready += 1,
                Ok(RecordDisposition::Skipped) => preview.records_skipped += 1,
                Err((kind, message)) => {
                    preview.records_failed += 1;
                    preview.issues.push(DatasetIssue {
                        record_number,
                        kind,
                        message,
                    });
                }
            },
            Err(error) => {
                preview.records_failed += 1;
                preview.issues.push(DatasetIssue {
                    record_number,
                    kind: IssueKind::InvalidJson,
                    message: error.to_string(),
                });
            }
        }
    }
    preview
}

fn base_preview(path: &Path, format: LegacyFormat) -> MigrationPreview {
    MigrationPreview {
        source_path: path.to_path_buf(),
        source_sha256: String::new(),
        format,
        records_read: 0,
        records_ready: 0,
        records_skipped: 0,
        records_failed: 0,
        issues: Vec::new(),
    }
}

fn failed_preview(
    path: &Path,
    format: LegacyFormat,
    records_read: usize,
    kind: IssueKind,
    message: String,
) -> MigrationPreview {
    MigrationPreview {
        source_path: path.to_path_buf(),
        source_sha256: String::new(),
        format,
        records_read,
        records_ready: 0,
        records_skipped: 0,
        records_failed: 1,
        issues: vec![DatasetIssue {
            record_number: records_read.max(1),
            kind,
            message,
        }],
    }
}

fn header_matches(headers: &StringRecord, expected: &[&str]) -> bool {
    headers.len() == expected.len()
        && headers
            .iter()
            .zip(expected)
            .all(|(actual, expected)| actual == *expected)
}

fn validate_ledger_record(record: &StringRecord) -> Result<(), (IssueKind, String)> {
    if record.len() != LEDGER_HEADER.len() {
        return invalid_record("Der Ledger-Datensatz enthält nicht 13 Felder.");
    }
    let schema_version = parse_u32(record, 0)?;
    if schema_version != 1 {
        return Err((
            IssueKind::UnsupportedVersion,
            format!("Ledger-Schema {schema_version} wird nicht unterstützt."),
        ));
    }
    parse_i32(record, 1)?;
    parse_identifier(record, 2)?;
    parse_timestamp(record, 3)?;
    let start_date = parse_date_field(record, 4)?;
    let end_date = parse_date_field(record, 5)?;
    if end_date < start_date {
        return invalid_record("Das Enddatum liegt vor dem Startdatum.");
    }
    parse_bool(record, 6)?;
    parse_bool(record, 7)?;
    parse_half_days(field(record, 8)?)?;
    validate_status(field(record, 9)?)?;
    if !field(record, 11)?.is_empty() {
        parse_timestamp(record, 11)?;
    }
    Ok(())
}

enum RecordDisposition {
    Ready,
    Skipped,
}

fn validate_history_record(
    record: &StringRecord,
) -> Result<RecordDisposition, (IssueKind, String)> {
    if record.len() != HISTORY_HEADER.len() {
        return invalid_record("Der History-Datensatz enthält nicht 12 Felder.");
    }
    parse_identifier(record, 0)?;
    parse_identifier(record, 1)?;
    parse_timestamp(record, 2)?;
    match field(record, 3)? {
        "Created" => {
            parse_i32(record, 4)?;
            let start_date = parse_date_field(record, 5)?;
            let end_date = parse_date_field(record, 6)?;
            if end_date < start_date {
                return invalid_record("Das Enddatum liegt vor dem Startdatum.");
            }
            parse_bool(record, 7)?;
            parse_bool(record, 8)?;
            parse_half_days(field(record, 9)?)?;
            Ok(RecordDisposition::Ready)
        }
        "Exported" => {
            if field(record, 10)?.is_empty() {
                return invalid_record("Ein Exportereignis enthält keinen PDF-Pfad.");
            }
            Ok(RecordDisposition::Ready)
        }
        "Approved" | "Archived" | "Deleted" => Ok(RecordDisposition::Ready),
        "Rejected" => Ok(RecordDisposition::Ready),
        _ => Ok(RecordDisposition::Skipped),
    }
}

fn validate_history_event_json(value: &Value) -> Result<RecordDisposition, (IssueKind, String)> {
    let Some(object) = value.as_object() else {
        return invalid_record("Das History-Ereignis muss ein JSON-Objekt sein.");
    };
    let event_type = object.get("$type").and_then(Value::as_str).ok_or_else(|| {
        (
            IssueKind::InvalidRecord,
            "Der Ereignistyp `$type` fehlt.".into(),
        )
    })?;
    if !matches!(
        event_type,
        "Created" | "Exported" | "Approved" | "Rejected" | "Archived" | "Deleted"
    ) {
        return Ok(RecordDisposition::Skipped);
    }
    for name in ["EventId", "RequestId"] {
        let identifier = object.get(name).and_then(Value::as_str).ok_or_else(|| {
            (
                IssueKind::InvalidRecord,
                format!("Das Ereignisfeld {name} fehlt oder ist kein Text."),
            )
        })?;
        if !is_guid(identifier) {
            return invalid_record(&format!(
                "Das Ereignisfeld {name} enthält keine gültige GUID."
            ));
        }
    }
    let timestamp = object
        .get("Timestamp")
        .and_then(Value::as_str)
        .ok_or_else(|| {
            (
                IssueKind::InvalidRecord,
                "Der Ereigniszeitpunkt fehlt oder ist kein Text.".into(),
            )
        })?;
    if DateTime::parse_from_rfc3339(timestamp).is_err() {
        return invalid_record("Der Ereigniszeitpunkt ist kein RFC-3339-Zeitstempel.");
    }
    if event_type == "Created" {
        let year = object.get("Year").and_then(Value::as_i64).ok_or_else(|| {
            (
                IssueKind::InvalidRecord,
                "Das Erstellungsereignis enthält kein gültiges Jahr.".into(),
            )
        })?;
        if !(1..=9999).contains(&year) {
            return invalid_record("Das Erstellungsereignis enthält kein gültiges Jahr.");
        }
        let start_date = object
            .get("StartDate")
            .and_then(Value::as_str)
            .and_then(parse_date_text)
            .ok_or_else(|| {
                (
                    IssueKind::InvalidRecord,
                    "Das Startdatum des Erstellungsereignisses ist ungültig.".into(),
                )
            })?;
        let end_date = object
            .get("EndDate")
            .and_then(Value::as_str)
            .and_then(parse_date_text)
            .ok_or_else(|| {
                (
                    IssueKind::InvalidRecord,
                    "Das Enddatum des Erstellungsereignisses ist ungültig.".into(),
                )
            })?;
        if end_date < start_date {
            return invalid_record("Das Enddatum liegt vor dem Startdatum.");
        }
        for name in ["StartHalfDay", "EndHalfDay"] {
            if object.get(name).and_then(Value::as_bool).is_none() {
                return invalid_record(&format!(
                    "Das Erstellungsereignis enthält kein gültiges Feld {name}."
                ));
            }
        }
        let days = object
            .get("CalculatedDays")
            .and_then(Value::as_number)
            .map(ToString::to_string);
        if days.as_deref().and_then(parse_half_days_text).is_none() {
            return invalid_record("Die berechneten Urlaubstage sind ungültig.");
        }
    }
    Ok(RecordDisposition::Ready)
}

fn field(record: &StringRecord, index: usize) -> Result<&str, (IssueKind, String)> {
    record
        .get(index)
        .ok_or_else(|| (IssueKind::InvalidRecord, format!("Feld {index} fehlt.")))
}

fn parse_identifier(record: &StringRecord, index: usize) -> Result<(), (IssueKind, String)> {
    let value = field(record, index)?;
    if is_guid(value) {
        Ok(())
    } else {
        invalid_record(&format!("Feld {index} enthält keine gültige GUID."))
    }
}

fn is_guid(value: &str) -> bool {
    let compact = value.replace('-', "");
    compact.len() == 32 && compact.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn parse_u32(record: &StringRecord, index: usize) -> Result<u32, (IssueKind, String)> {
    field(record, index)?.parse().map_err(|_| {
        (
            IssueKind::InvalidRecord,
            format!("Feld {index} enthält keine gültige Zahl."),
        )
    })
}

fn parse_i32(record: &StringRecord, index: usize) -> Result<i32, (IssueKind, String)> {
    field(record, index)?.parse().map_err(|_| {
        (
            IssueKind::InvalidRecord,
            format!("Feld {index} enthält keine gültige Zahl."),
        )
    })
}

fn parse_timestamp(record: &StringRecord, index: usize) -> Result<(), (IssueKind, String)> {
    DateTime::parse_from_rfc3339(field(record, index)?)
        .map(|_| ())
        .map_err(|_| {
            (
                IssueKind::InvalidRecord,
                format!("Feld {index} enthält keinen RFC-3339-Zeitstempel."),
            )
        })
}

fn parse_date_field(record: &StringRecord, index: usize) -> Result<NaiveDate, (IssueKind, String)> {
    parse_date_text(field(record, index)?).ok_or_else(|| {
        (
            IssueKind::InvalidRecord,
            format!("Feld {index} enthält kein gültiges Datum."),
        )
    })
}

fn parse_date_text(value: &str) -> Option<NaiveDate> {
    let date = value.get(..10).unwrap_or(value);
    NaiveDate::parse_from_str(date, "%Y-%m-%d").ok()
}

fn parse_bool(record: &StringRecord, index: usize) -> Result<(), (IssueKind, String)> {
    match field(record, index)? {
        "true" | "false" | "True" | "False" => Ok(()),
        _ => invalid_record(&format!("Feld {index} enthält keinen booleschen Wert.")),
    }
}

fn parse_half_days(record: &str) -> Result<u32, (IssueKind, String)> {
    parse_half_days_text(record).ok_or_else(|| {
        (
            IssueKind::InvalidRecord,
            "Urlaubstage müssen in halben Tagen angegeben sein.".into(),
        )
    })
}

fn parse_half_days_text(value: &str) -> Option<u32> {
    let (whole, fraction) = value.split_once('.').unwrap_or((value, "0"));
    let whole = whole.parse::<u32>().ok()?;
    let half = match fraction {
        "0" | "00" | "0000000" => 0,
        "5" | "50" | "5000000" => 1,
        _ => return None,
    };
    whole.checked_mul(2)?.checked_add(half)
}

fn validate_status(value: &str) -> Result<(), (IssueKind, String)> {
    if matches!(
        value,
        "Draft" | "Exported" | "Approved" | "Rejected" | "Archived" | "Deleted"
    ) {
        Ok(())
    } else {
        Err((
            IssueKind::UnsupportedStatus,
            format!("Der Status {value:?} wird nicht unterstützt."),
        ))
    }
}

fn invalid_record<T>(message: &str) -> Result<T, (IssueKind, String)> {
    Err((IssueKind::InvalidRecord, message.into()))
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicU64, Ordering};

    use super::*;
    use crate::backup::backup_source_to_new;

    static NEXT_TEMP_ID: AtomicU64 = AtomicU64::new(1);

    struct TempDirectory(PathBuf);

    impl TempDirectory {
        fn new() -> Self {
            let id = NEXT_TEMP_ID.fetch_add(1, Ordering::Relaxed);
            let path = std::env::temp_dir().join(format!(
                "urlaubstool-migration-preview-{}-{id}",
                std::process::id()
            ));
            fs::create_dir_all(&path).expect("create temporary test directory");
            Self(path)
        }
    }

    impl Drop for TempDirectory {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn req_mig_001_settings_v2_and_name_only_v1_are_detected() {
        let directory = TempDirectory::new();
        let current = directory.0.join("settings.json");
        fs::write(&current, br#"{"Version":2,"Name":"Ada Beispiel"}"#).expect("write v2");
        let preview = preview_file(&current).expect("preview v2");
        assert_eq!(
            preview.format,
            LegacyFormat::SettingsJson { schema_version: 2 }
        );
        assert_eq!(preview.records_ready, 1);
        assert!(preview.can_import());

        fs::write(&current, br#"{"Name":"Ada Beispiel"}"#).expect("write v1");
        let preview = preview_file(&current).expect("preview v1");
        assert_eq!(
            preview.format,
            LegacyFormat::SettingsJson { schema_version: 1 }
        );
    }

    #[test]
    fn req_mig_003_ledger_preview_counts_ready_and_invalid_records() {
        let directory = TempDirectory::new();
        let path = directory.0.join("ledger.csv");
        let content = format!(
            "{}\n1,2026,550e8400-e29b-41d4-a716-446655440000,2026-01-02T03:04:05Z,2026-11-09,2026-11-10,true,false,1.5,Approved,,,\n1,2026,not-a-guid,2026-01-02T03:04:05Z,2026-11-09,2026-11-10,false,false,1.0,Approved,,,\n",
            LEDGER_HEADER.join(",")
        );
        fs::write(&path, content).expect("write ledger");

        let preview = preview_file(&path).expect("preview ledger");
        assert_eq!(
            preview.format,
            LegacyFormat::LedgerCsv { schema_version: 1 }
        );
        assert_eq!(preview.records_read, 2);
        assert_eq!(preview.records_ready, 1);
        assert_eq!(preview.records_failed, 1);
        assert!(!preview.can_import());
        assert_eq!(preview.issues[0].record_number, 3);
    }

    #[test]
    fn req_mig_003_history_csv_handles_quoted_commas_and_unknown_events() {
        let directory = TempDirectory::new();
        let path = directory.0.join("history.csv");
        let content = format!(
            "{}\n550e8400-e29b-41d4-a716-446655440000,550e8400-e29b-41d4-a716-446655440001,2026-01-02T03:04:05Z,Rejected,,,,,,,,\"Grund, mit Komma\"\n550e8400-e29b-41d4-a716-446655440002,550e8400-e29b-41d4-a716-446655440001,2026-01-02T03:04:06Z,FutureEvent,,,,,,,,\n",
            HISTORY_HEADER.join(",")
        );
        fs::write(&path, content).expect("write history csv");

        let preview = preview_file(&path).expect("preview history csv");
        assert_eq!(preview.records_read, 2);
        assert_eq!(preview.records_ready, 1);
        assert_eq!(preview.records_skipped, 1);
        assert_eq!(preview.records_failed, 0);
    }

    #[test]
    fn req_mig_003_history_jsonl_reports_corrupt_lines_with_line_numbers() {
        let directory = TempDirectory::new();
        let path = directory.0.join("history.jsonl");
        let content = "{\"$type\":\"Approved\",\"EventId\":\"550e8400-e29b-41d4-a716-446655440000\",\"RequestId\":\"550e8400-e29b-41d4-a716-446655440001\",\"Timestamp\":\"2026-01-02T03:04:05Z\"}\n{broken\n";
        fs::write(&path, content).expect("write history jsonl");

        let preview = preview_file(&path).expect("preview history jsonl");
        assert_eq!(preview.records_ready, 1);
        assert_eq!(preview.records_failed, 1);
        assert_eq!(preview.issues[0].record_number, 2);
        assert_eq!(preview.issues[0].kind, IssueKind::InvalidJson);
    }

    #[test]
    fn req_mig_001_utf8_bom_is_supported_but_invalid_encoding_is_not_guessed() {
        let directory = TempDirectory::new();
        let path = directory.0.join("settings.json");
        fs::write(&path, b"\xEF\xBB\xBF{\"Version\":2,\"Name\":\"Ada\"}").expect("write BOM JSON");
        assert!(preview_file(&path).expect("preview BOM").can_import());

        fs::write(&path, [0xFF, 0xFE, 0x00]).expect("write unsupported encoding");
        assert!(matches!(
            preview_file(&path),
            Err(PreviewError::InvalidEncoding(_))
        ));
    }

    #[test]
    fn req_mig_002_preview_hash_matches_non_mutating_source_backup() {
        let directory = TempDirectory::new();
        let source = directory.0.join("settings.json");
        let backup = directory.0.join("settings.backup.json");
        let bytes = br#"{"Version":2,"Name":"Ada Beispiel"}"#;
        fs::write(&source, bytes).expect("write source");

        let preview = preview_file(&source).expect("preview settings");
        let receipt = backup_source_to_new(&source, &backup).expect("backup source");

        assert_eq!(preview.source_sha256, receipt.sha256);
        assert_eq!(fs::read(&source).expect("source intact"), bytes);
    }
}
