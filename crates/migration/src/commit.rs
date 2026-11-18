use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::Path;

use chrono::{DateTime, Datelike, Duration, NaiveDate, Utc};
use csv::{ReaderBuilder, StringRecord};
use serde_json::Value;
use thiserror::Error;
use urlaubstool_application::{
    LegacyImportBatch, MigratedExportRecord, MigratedRequest, MigratedStatusEvent,
    MigrationRepository, RequestStatus, UserSettings,
};

use crate::backup::{backup_source_to_new, SourceBackup, SourceBackupError};
use crate::preview::{IssueKind, LegacyFormat, MigrationPreview, PreviewError};

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

#[derive(Debug, Error)]
pub enum MigrationCommitError {
    #[error("Der Migrationspreview enthält fehlerhafte Datensätze.")]
    PreviewHasFailures,
    #[error(
        "Der Preview enthält übersprungene Datensätze; bestätige deren Ausschluss ausdrücklich."
    )]
    SkippedRecordsNeedConfirmation,
    #[error(
        "Die Quelldatei wurde seit der Vorschau verändert; die Sicherung blieb erhalten unter {0}."
    )]
    SourceChanged(String),
    #[error("Die Quelldatei konnte nicht aus der Sicherung gelesen werden: {0}")]
    BackupRead(String),
    #[error("Der Migrationsbatch ist ungültig: {0}")]
    InvalidBatch(String),
    #[error("Sicherung fehlgeschlagen: {0}")]
    Backup(#[from] SourceBackupError),
    #[error("Migrationsvorschau fehlgeschlagen: {0}")]
    Preview(#[from] PreviewError),
    #[error("SQLite-Import fehlgeschlagen: {0}")]
    Import(String),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MigrationCommitReceipt {
    pub backup: SourceBackup,
    pub settings_imported: bool,
    pub requests_imported: u64,
    pub status_events_imported: u64,
    pub exports_imported: u64,
    pub already_imported: bool,
}

#[derive(Debug, Clone)]
enum LegacyEventKind {
    Created {
        year: i32,
        start_date: NaiveDate,
        end_date: NaiveDate,
        start_half_day: bool,
        end_half_day: bool,
        requested_half_units: u32,
        aza_dates: Vec<NaiveDate>,
    },
    Exported {
        pdf_path: String,
    },
    Approved,
    Rejected {
        reason: Option<String>,
    },
    Archived,
    Deleted,
}

#[derive(Debug, Clone)]
struct LegacyEvent {
    event_id: String,
    request_id: String,
    timestamp: DateTime<Utc>,
    kind: LegacyEventKind,
}

pub fn commit_preview<R: MigrationRepository>(
    repository: &mut R,
    preview: &MigrationPreview,
    backup_path: impl AsRef<Path>,
    started_at_utc: DateTime<Utc>,
    committed_at_utc: DateTime<Utc>,
    confirm_skipped_records: bool,
) -> Result<MigrationCommitReceipt, MigrationCommitError> {
    if preview.records_failed > 0 || preview.records_ready == 0 {
        return Err(MigrationCommitError::PreviewHasFailures);
    }
    if preview.records_skipped > 0 && !confirm_skipped_records {
        return Err(MigrationCommitError::SkippedRecordsNeedConfirmation);
    }

    let backup = backup_source_to_new(&preview.source_path, backup_path)?;
    if backup.sha256 != preview.source_sha256 {
        return Err(MigrationCommitError::SourceChanged(
            backup.destination.to_string_lossy().into_owned(),
        ));
    }
    let contents = fs::read(&backup.destination)
        .map_err(|error| MigrationCommitError::BackupRead(error.to_string()))?;
    let contents = decode_utf8(&contents).ok_or_else(|| {
        MigrationCommitError::BackupRead("Die Sicherung ist kein UTF-8-Dokument.".into())
    })?;
    let batch = build_batch(
        preview,
        contents,
        started_at_utc,
        committed_at_utc,
        confirm_skipped_records,
    )?;
    let report = repository
        .apply_legacy_import(&batch)
        .map_err(|error| MigrationCommitError::Import(error.to_string()))?;

    Ok(MigrationCommitReceipt {
        backup,
        settings_imported: report.settings_imported,
        requests_imported: report.requests_imported,
        status_events_imported: report.status_events_imported,
        exports_imported: report.exports_imported,
        already_imported: report.already_imported,
    })
}

/// Liest eine (gesicherte) Altdatei erneut und liefert genau die Datensätze, die ein
/// Import übernimmt. Grundlage für den Datenabgleich nach dem Import (REQ-MIG-004).
pub fn expected_batch(
    preview: &MigrationPreview,
    source: impl AsRef<Path>,
    confirm_skipped_records: bool,
) -> Result<LegacyImportBatch, MigrationCommitError> {
    let bytes = fs::read(source.as_ref())
        .map_err(|error| MigrationCommitError::BackupRead(error.to_string()))?;
    let contents = decode_utf8(&bytes).ok_or_else(|| {
        MigrationCommitError::BackupRead("Die Datei ist kein UTF-8-Dokument.".into())
    })?;
    let epoch = DateTime::<Utc>::UNIX_EPOCH;
    build_batch(preview, contents, epoch, epoch, confirm_skipped_records)
}

fn build_batch(
    preview: &MigrationPreview,
    contents: &str,
    started_at_utc: DateTime<Utc>,
    committed_at_utc: DateTime<Utc>,
    skipped_records_confirmed: bool,
) -> Result<LegacyImportBatch, MigrationCommitError> {
    let migration_id = format!("{}:{}", format_code(&preview.format), preview.source_sha256);
    let mut batch = LegacyImportBatch {
        migration_id,
        source_path: preview.source_path.clone(),
        source_sha256: preview.source_sha256.clone(),
        source_format: format_code(&preview.format).to_owned(),
        started_at_utc,
        committed_at_utc,
        records_read: preview.records_read,
        records_ready: preview.records_ready,
        records_skipped: preview.records_skipped,
        records_failed: preview.records_failed,
        skipped_records_confirmed,
        settings: None,
        requests: Vec::new(),
        status_events: Vec::new(),
        exports: Vec::new(),
    };

    match &preview.format {
        LegacyFormat::SettingsJson { schema_version } => {
            batch.settings = Some(parse_settings(contents, *schema_version)?);
        }
        LegacyFormat::LedgerCsv { schema_version: 1 } => {
            parse_ledger(contents, &mut batch)?;
        }
        LegacyFormat::LedgerCsv { schema_version } => {
            return Err(MigrationCommitError::InvalidBatch(format!(
                "Ledger-Schema {schema_version} wird nicht unterstützt."
            )));
        }
        LegacyFormat::HistoryCsv => {
            let events = parse_history_csv(contents)?;
            project_history(events, &mut batch)?;
        }
        LegacyFormat::HistoryJsonl => {
            let events = parse_history_jsonl(contents)?;
            project_history(events, &mut batch)?;
        }
    }
    Ok(batch)
}

fn parse_settings(
    contents: &str,
    schema_version: u32,
) -> Result<UserSettings, MigrationCommitError> {
    let value: Value = serde_json::from_str(contents)
        .map_err(|error| MigrationCommitError::InvalidBatch(error.to_string()))?;
    let object = value.as_object().ok_or_else(|| {
        MigrationCommitError::InvalidBatch("Einstellungen sind kein JSON-Objekt.".into())
    })?;

    let mut settings = UserSettings::default();
    let name = required_or_optional_string(object, "Name")?.unwrap_or_default();
    let first_name = required_or_optional_string(object, "Vorname")?.unwrap_or_default();
    let last_name = required_or_optional_string(object, "Nachname")?.unwrap_or_default();
    if !first_name.trim().is_empty() || !last_name.trim().is_empty() {
        settings.first_name = first_name;
        settings.last_name = last_name;
    } else if schema_version == 1 || !name.trim().is_empty() {
        let parts = name.split_whitespace().collect::<Vec<_>>();
        match parts.as_slice() {
            [] => {}
            [surname] => settings.last_name = (*surname).to_owned(),
            _ => {
                settings.last_name = parts[parts.len() - 1].to_owned();
                settings.first_name = parts[..parts.len() - 1].join(" ");
            }
        }
    }

    settings.address = required_or_optional_string(object, "Adresse")?;
    settings.department = required_or_optional_string(object, "Abteilung")?;
    settings.personnel_number = required_or_optional_string(object, "Personalnummer")?;
    settings.class_name = required_or_optional_string(object, "Klasse")?;
    settings.annual_entitlement_half_units = object
        .get("Jahresurlaub")
        .map(parse_decimal_half_units)
        .transpose()?
        .unwrap_or_default();
    settings.federal_state_code = required_or_optional_string(object, "Bundesland")?;
    settings.student_mode = match object.get("StudentActive") {
        None => false,
        Some(Value::Bool(value)) => *value,
        Some(_) => {
            return Err(MigrationCommitError::InvalidBatch(
                "StudentActive muss boolesch sein.".into(),
            ))
        }
    };
    settings.export_path = required_or_optional_string(object, "ExportPath")?.map(Into::into);
    settings.workday_mask = parse_workdays(object.get("Workdays"))?;
    settings.vocational_school_week = parse_school_week(object.get("VocationalSchool"))?;
    if let Some(color_settings) = object.get("ColorSettings") {
        let colors = color_settings.as_object().ok_or_else(|| {
            MigrationCommitError::InvalidBatch("ColorSettings muss ein Objekt sein.".into())
        })?;
        settings.calendar_colors = colors
            .iter()
            .map(|(name, color)| {
                let value = color.as_str().ok_or_else(|| {
                    MigrationCommitError::InvalidBatch(format!(
                        "ColorSettings-Feld {name} muss Text enthalten."
                    ))
                })?;
                Ok((name.clone(), value.to_owned()))
            })
            .collect::<Result<_, MigrationCommitError>>()?;
    }
    Ok(settings)
}

fn parse_workdays(value: Option<&Value>) -> Result<u8, MigrationCommitError> {
    let Some(days) = value else {
        return Ok(0b0001_1111);
    };
    let days = days
        .as_array()
        .ok_or_else(|| MigrationCommitError::InvalidBatch("Workdays ist kein Array.".into()))?;
    let mut mask = 0;
    for day in days {
        let monday_index = match day {
            Value::String(name) => weekday_index(name),
            Value::Number(number) => number.as_u64().and_then(|value| match value {
                0 => Some(6),
                1..=6 => Some(value as u8 - 1),
                _ => None,
            }),
            _ => None,
        }
        .ok_or_else(|| {
            MigrationCommitError::InvalidBatch(
                "Workdays enthält einen unbekannten Wochentag.".into(),
            )
        })?;
        mask |= 1 << monday_index;
    }
    Ok(mask)
}

fn parse_school_week(value: Option<&Value>) -> Result<[u8; 7], MigrationCommitError> {
    let mut week = [0; 7];
    let Some(days) = value else {
        return Ok(week);
    };
    let days = days.as_object().ok_or_else(|| {
        MigrationCommitError::InvalidBatch("VocationalSchool ist kein Objekt.".into())
    })?;
    for (name, value) in days {
        let index = weekday_index(name).ok_or_else(|| {
            MigrationCommitError::InvalidBatch(format!("Unbekannter Berufsschultag {name:?}."))
        })?;
        let school_type = match value {
            Value::String(kind) => match kind.as_str() {
                "None" => 0,
                "Half" => 1,
                "Full" => 2,
                _ => {
                    return Err(MigrationCommitError::InvalidBatch(format!(
                        "Unbekannter Berufsschultyp {kind:?}."
                    )))
                }
            },
            Value::Number(number) => number
                .as_u64()
                .filter(|value| *value <= 2)
                .map(|value| value as u8)
                .ok_or_else(|| {
                    MigrationCommitError::InvalidBatch("Ungültiger Berufsschultyp.".into())
                })?,
            _ => {
                return Err(MigrationCommitError::InvalidBatch(
                    "Ungültiger Berufsschultyp.".into(),
                ))
            }
        };
        week[index as usize] = school_type;
    }
    Ok(week)
}

fn weekday_index(name: &str) -> Option<u8> {
    match name {
        "Monday" | "Mon" => Some(0),
        "Tuesday" | "Tue" => Some(1),
        "Wednesday" | "Wed" => Some(2),
        "Thursday" | "Thu" => Some(3),
        "Friday" | "Fri" => Some(4),
        "Saturday" | "Sat" => Some(5),
        "Sunday" | "Sun" => Some(6),
        _ => None,
    }
}

fn parse_decimal_half_units(value: &Value) -> Result<u32, MigrationCommitError> {
    let decimal = match value {
        Value::Number(number) => number.to_string(),
        Value::String(text) => text.clone(),
        _ => {
            return Err(MigrationCommitError::InvalidBatch(
                "Jahresurlaub ist keine Dezimalzahl.".into(),
            ))
        }
    };
    parse_half_units(&decimal).ok_or_else(|| {
        MigrationCommitError::InvalidBatch("Jahresurlaub ist keine exakte halbe Zahl.".into())
    })
}

fn parse_half_units(value: &str) -> Option<u32> {
    let (whole, fraction) = value.split_once('.').unwrap_or((value, "0"));
    let whole = whole.parse::<u32>().ok()?;
    let fraction = fraction.trim_end_matches('0');
    let extra = match fraction {
        "" => 0,
        "5" => 1,
        _ => return None,
    };
    whole.checked_mul(2)?.checked_add(extra)
}

fn required_or_optional_string(
    object: &serde_json::Map<String, Value>,
    name: &str,
) -> Result<Option<String>, MigrationCommitError> {
    match object.get(name) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(value)) => Ok((!value.trim().is_empty()).then(|| value.clone())),
        Some(_) => Err(MigrationCommitError::InvalidBatch(format!(
            "Settings-Feld {name} muss Text oder null sein."
        ))),
    }
}

fn format_code(format: &LegacyFormat) -> &'static str {
    match format {
        LegacyFormat::SettingsJson { schema_version: 1 } => "settings_json_v1",
        LegacyFormat::SettingsJson { .. } => "settings_json_v2",
        LegacyFormat::LedgerCsv { .. } => "ledger_csv_v1",
        LegacyFormat::HistoryCsv => "history_csv",
        LegacyFormat::HistoryJsonl => "history_jsonl",
    }
}

fn build_ledger_events(
    record: &StringRecord,
    line_number: usize,
    batch: &mut LegacyImportBatch,
) -> Result<(), MigrationCommitError> {
    if record.len() != 13 {
        return Err(MigrationCommitError::InvalidBatch(format!(
            "Ledger-Zeile {line_number} enthält nicht 13 Felder."
        )));
    }
    let schema =
        parse_u32(record, 0).map_err(|(_, message)| MigrationCommitError::InvalidBatch(message))?;
    if schema != 1 {
        return Err(MigrationCommitError::InvalidBatch(format!(
            "Ledger-Schema {schema} wird nicht unterstützt."
        )));
    }
    let year =
        parse_i32(record, 1).map_err(|(_, message)| MigrationCommitError::InvalidBatch(message))?;
    let request_id = field(record, 2)?.to_owned();
    let created_at = parse_timestamp_text(field(record, 3)?)?;
    let start_date = parse_date_text(field(record, 4)?).ok_or_else(|| {
        MigrationCommitError::InvalidBatch(format!(
            "Ledger-Zeile {line_number} hat ein ungültiges Startdatum."
        ))
    })?;
    let end_date = parse_date_text(field(record, 5)?).ok_or_else(|| {
        MigrationCommitError::InvalidBatch(format!(
            "Ledger-Zeile {line_number} hat ein ungültiges Enddatum."
        ))
    })?;
    if year != start_date.year() || end_date < start_date {
        return Err(MigrationCommitError::InvalidBatch(format!(
            "Ledger-Zeile {line_number} hat widersprüchliche Datumswerte."
        )));
    }
    let start_half_day = parse_bool_text(field(record, 6)?)?;
    let end_half_day = parse_bool_text(field(record, 7)?)?;
    let requested_half_units = parse_half_units(field(record, 8)?).ok_or_else(|| {
        MigrationCommitError::InvalidBatch(format!(
            "Ledger-Zeile {line_number} hat ungültige Urlaubstage."
        ))
    })?;
    let status = parse_status(field(record, 9)?)?;
    let rejection_reason = nonempty_optional(field(record, 10)?);
    let archived_at_utc = optional_timestamp(field(record, 11)?)?;
    let pdf_path = nonempty_optional(field(record, 12)?);

    let event_base = format!("{}:{line_number}", batch.migration_id);
    let mut status_events = vec![MigratedStatusEvent {
        event_id: format!("{event_base}:created"),
        request_id: request_id.clone(),
        previous_status: None,
        new_status: RequestStatus::Draft,
        occurred_at_utc: created_at,
        rejection_reason: None,
    }];
    let mut exports = Vec::new();
    let mut previous_status = RequestStatus::Draft;
    let mut updated_at = created_at;

    if let Some(path) = pdf_path {
        let exported_at = add_seconds(created_at, 1)?;
        let event_id = format!("{event_base}:exported");
        status_events.push(MigratedStatusEvent {
            event_id: event_id.clone(),
            request_id: request_id.clone(),
            previous_status: Some(previous_status),
            new_status: RequestStatus::Exported,
            occurred_at_utc: exported_at,
            rejection_reason: None,
        });
        exports.push(MigratedExportRecord {
            export_id: event_id,
            request_id: request_id.clone(),
            file_path: path.into(),
            content_sha256: None,
            exported_at_utc: exported_at,
        });
        previous_status = RequestStatus::Exported;
        updated_at = exported_at;
    }

    let final_status =
        if status == RequestStatus::Archived && previous_status != RequestStatus::Approved {
            RequestStatus::Approved
        } else {
            status
        };
    if final_status != RequestStatus::Draft && final_status != previous_status {
        let event_time = match final_status {
            RequestStatus::Archived => archived_at_utc.unwrap_or(add_seconds(created_at, 3)?),
            _ => add_seconds(created_at, status_events.len() as i64 + 1)?,
        };
        status_events.push(MigratedStatusEvent {
            event_id: format!(
                "{event_base}:{}",
                final_status.as_str().to_ascii_lowercase()
            ),
            request_id: request_id.clone(),
            previous_status: Some(previous_status),
            new_status: final_status,
            occurred_at_utc: event_time,
            rejection_reason: (final_status == RequestStatus::Rejected)
                .then(|| rejection_reason.clone())
                .flatten(),
        });
        previous_status = final_status;
        updated_at = event_time;
    }
    if status == RequestStatus::Archived && previous_status == RequestStatus::Approved {
        let archived_at = archived_at_utc.unwrap_or(add_seconds(updated_at, 1)?);
        status_events.push(MigratedStatusEvent {
            event_id: format!("{event_base}:archived"),
            request_id: request_id.clone(),
            previous_status: Some(RequestStatus::Approved),
            new_status: RequestStatus::Archived,
            occurred_at_utc: archived_at,
            rejection_reason: None,
        });
        updated_at = archived_at;
    }

    batch.requests.push(MigratedRequest {
        request_id: request_id.clone(),
        year,
        start_date,
        end_date,
        start_half_day,
        end_half_day,
        requested_half_units,
        status,
        rejection_reason,
        archived_at_utc,
        created_at_utc: created_at,
        updated_at_utc: updated_at,
        aza_dates: Vec::new(),
    });
    batch.status_events.extend(status_events);
    batch.exports.extend(exports);
    Ok(())
}

fn parse_ledger(contents: &str, batch: &mut LegacyImportBatch) -> Result<(), MigrationCommitError> {
    let mut reader = ReaderBuilder::new()
        .has_headers(true)
        .flexible(true)
        .from_reader(contents.as_bytes());
    let headers = reader
        .headers()
        .map_err(|error| MigrationCommitError::InvalidBatch(error.to_string()))?;
    if headers.len() != LEDGER_HEADER.len()
        || !headers
            .iter()
            .zip(LEDGER_HEADER)
            .all(|(actual, expected)| actual == expected)
    {
        return Err(MigrationCommitError::InvalidBatch(
            "Der Ledger-Header hat sich seit der Vorschau verändert.".into(),
        ));
    }
    for (index, record) in reader.records().enumerate() {
        let record =
            record.map_err(|error| MigrationCommitError::InvalidBatch(error.to_string()))?;
        build_ledger_events(&record, index + 2, batch)?;
    }
    Ok(())
}

fn parse_history_csv(contents: &str) -> Result<Vec<LegacyEvent>, MigrationCommitError> {
    let mut reader = ReaderBuilder::new()
        .has_headers(true)
        .flexible(true)
        .from_reader(contents.as_bytes());
    let headers = reader
        .headers()
        .map_err(|error| MigrationCommitError::InvalidBatch(error.to_string()))?;
    if headers.len() != HISTORY_HEADER.len()
        || !headers
            .iter()
            .zip(HISTORY_HEADER)
            .all(|(actual, expected)| actual == expected)
    {
        return Err(MigrationCommitError::InvalidBatch(
            "Der History-CSV-Header hat sich seit der Vorschau verändert.".into(),
        ));
    }
    let mut events = Vec::new();
    for record in reader.records() {
        let record =
            record.map_err(|error| MigrationCommitError::InvalidBatch(error.to_string()))?;
        if let Some(event) = parse_history_csv_event(&record)? {
            events.push(event);
        }
    }
    Ok(events)
}

fn parse_history_csv_event(
    record: &StringRecord,
) -> Result<Option<LegacyEvent>, MigrationCommitError> {
    if record.len() != HISTORY_HEADER.len() {
        return Err(MigrationCommitError::InvalidBatch(
            "Der History-CSV-Datensatz enthält nicht 12 Felder.".into(),
        ));
    }
    let event_id = field(record, 0)?.to_owned();
    let request_id = field(record, 1)?.to_owned();
    let timestamp = parse_timestamp_text(field(record, 2)?)?;
    let kind = match field(record, 3)? {
        "Created" => {
            let year = parse_i32(record, 4)
                .map_err(|(_, message)| MigrationCommitError::InvalidBatch(message))?;
            let start_date = parse_date_text(field(record, 5)?).ok_or_else(|| {
                MigrationCommitError::InvalidBatch("Startdatum im History-CSV ist ungültig.".into())
            })?;
            let end_date = parse_date_text(field(record, 6)?).ok_or_else(|| {
                MigrationCommitError::InvalidBatch("Enddatum im History-CSV ist ungültig.".into())
            })?;
            if year != start_date.year() || end_date < start_date {
                return Err(MigrationCommitError::InvalidBatch(
                    "Erstellungsereignis hat widersprüchliche Datumswerte.".into(),
                ));
            }
            LegacyEventKind::Created {
                year,
                start_date,
                end_date,
                start_half_day: parse_bool_text(field(record, 7)?)?,
                end_half_day: parse_bool_text(field(record, 8)?)?,
                requested_half_units: parse_half_units(field(record, 9)?).ok_or_else(|| {
                    MigrationCommitError::InvalidBatch(
                        "Berechnete Urlaubstage sind ungültig.".into(),
                    )
                })?,
                aza_dates: Vec::new(),
            }
        }
        "Exported" => LegacyEventKind::Exported {
            pdf_path: nonempty(field(record, 10)?)?,
        },
        "Approved" => LegacyEventKind::Approved,
        "Rejected" => LegacyEventKind::Rejected {
            reason: nonempty_optional(field(record, 11)?),
        },
        "Archived" => LegacyEventKind::Archived,
        "Deleted" => LegacyEventKind::Deleted,
        _ => return Ok(None),
    };
    Ok(Some(LegacyEvent {
        event_id,
        request_id,
        timestamp,
        kind,
    }))
}

fn parse_history_jsonl(contents: &str) -> Result<Vec<LegacyEvent>, MigrationCommitError> {
    let mut events = Vec::new();
    for (index, line) in contents.lines().enumerate() {
        if line.trim().is_empty() {
            continue;
        }
        let value: Value = serde_json::from_str(line).map_err(|error| {
            MigrationCommitError::InvalidBatch(format!(
                "History-JSONL-Zeile {}: {error}",
                index + 1
            ))
        })?;
        if let Some(event) = parse_history_json_event(&value)? {
            events.push(event);
        }
    }
    Ok(events)
}

fn parse_history_json_event(value: &Value) -> Result<Option<LegacyEvent>, MigrationCommitError> {
    let object = value.as_object().ok_or_else(|| {
        MigrationCommitError::InvalidBatch("History-Ereignis ist kein JSON-Objekt.".into())
    })?;
    let event_type = object.get("$type").and_then(Value::as_str).ok_or_else(|| {
        MigrationCommitError::InvalidBatch("History-Ereignis hat keinen `$type`.".into())
    })?;
    if !matches!(
        event_type,
        "Created" | "Exported" | "Approved" | "Rejected" | "Archived" | "Deleted"
    ) {
        return Ok(None);
    }
    let get_string = |key: &str| {
        object.get(key).and_then(Value::as_str).ok_or_else(|| {
            MigrationCommitError::InvalidBatch(format!("History-Ereignis hat kein Textfeld {key}."))
        })
    };
    let event_id = get_string("EventId")?.to_owned();
    let request_id = get_string("RequestId")?.to_owned();
    let timestamp = parse_timestamp_text(get_string("Timestamp")?)?;
    let kind = match event_type {
        "Created" => {
            let year = object
                .get("Year")
                .and_then(Value::as_i64)
                .and_then(|year| i32::try_from(year).ok())
                .ok_or_else(|| {
                    MigrationCommitError::InvalidBatch(
                        "History-Created-Ereignis hat kein gültiges Jahr.".into(),
                    )
                })?;
            let start_date = parse_date_text(get_string("StartDate")?).ok_or_else(|| {
                MigrationCommitError::InvalidBatch(
                    "Startdatum im History-JSONL ist ungültig.".into(),
                )
            })?;
            let end_date = parse_date_text(get_string("EndDate")?).ok_or_else(|| {
                MigrationCommitError::InvalidBatch("Enddatum im History-JSONL ist ungültig.".into())
            })?;
            if year != start_date.year() || end_date < start_date {
                return Err(MigrationCommitError::InvalidBatch(
                    "Erstellungsereignis hat widersprüchliche Datumswerte.".into(),
                ));
            }
            let aza_dates = object
                .get("AzaDates")
                .and_then(Value::as_array)
                .map(|dates| {
                    dates
                        .iter()
                        .map(|value| {
                            value.as_str().and_then(parse_date_text).ok_or_else(|| {
                                MigrationCommitError::InvalidBatch(
                                    "AZA-Datum im History-JSONL ist ungültig.".into(),
                                )
                            })
                        })
                        .collect::<Result<Vec<_>, _>>()
                })
                .transpose()?
                .unwrap_or_default();
            LegacyEventKind::Created {
                year,
                start_date,
                end_date,
                start_half_day: object
                    .get("StartHalfDay")
                    .and_then(Value::as_bool)
                    .ok_or_else(|| {
                        MigrationCommitError::InvalidBatch(
                            "StartHalfDay im History-JSONL ist ungültig.".into(),
                        )
                    })?,
                end_half_day: object
                    .get("EndHalfDay")
                    .and_then(Value::as_bool)
                    .ok_or_else(|| {
                        MigrationCommitError::InvalidBatch(
                            "EndHalfDay im History-JSONL ist ungültig.".into(),
                        )
                    })?,
                requested_half_units: object
                    .get("CalculatedDays")
                    .and_then(Value::as_number)
                    .map(ToString::to_string)
                    .and_then(|value| parse_half_units(&value))
                    .ok_or_else(|| {
                        MigrationCommitError::InvalidBatch(
                            "CalculatedDays im History-JSONL ist ungültig.".into(),
                        )
                    })?,
                aza_dates,
            }
        }
        "Exported" => LegacyEventKind::Exported {
            pdf_path: nonempty(get_string("PdfPath")?)?,
        },
        "Approved" => LegacyEventKind::Approved,
        "Rejected" => LegacyEventKind::Rejected {
            reason: object
                .get("RejectionReason")
                .and_then(Value::as_str)
                .and_then(nonempty_optional),
        },
        "Archived" => LegacyEventKind::Archived,
        "Deleted" => LegacyEventKind::Deleted,
        _ => return Ok(None),
    };
    Ok(Some(LegacyEvent {
        event_id,
        request_id,
        timestamp,
        kind,
    }))
}

fn project_history(
    mut events: Vec<LegacyEvent>,
    batch: &mut LegacyImportBatch,
) -> Result<(), MigrationCommitError> {
    events.sort_by_key(|event| event.timestamp);
    let mut requests = BTreeMap::<String, MigratedRequest>::new();
    let mut event_ids = BTreeSet::new();
    for event in events {
        if !event_ids.insert(event.event_id.clone()) {
            return Err(MigrationCommitError::InvalidBatch(format!(
                "History-Ereignis-ID {} kommt mehrfach vor.",
                event.event_id
            )));
        }
        match event.kind {
            LegacyEventKind::Created {
                year,
                start_date,
                end_date,
                start_half_day,
                end_half_day,
                requested_half_units,
                aza_dates,
            } => {
                if requests.contains_key(&event.request_id) {
                    return Err(MigrationCommitError::InvalidBatch(format!(
                        "Request-ID {} hat mehr als ein Created-Ereignis.",
                        event.request_id
                    )));
                }
                if aza_dates
                    .iter()
                    .any(|date| *date < start_date || *date > end_date)
                {
                    return Err(MigrationCommitError::InvalidBatch(format!(
                        "Request-ID {} enthält ein AZA-Datum außerhalb des Zeitraums.",
                        event.request_id
                    )));
                }
                requests.insert(
                    event.request_id.clone(),
                    MigratedRequest {
                        request_id: event.request_id.clone(),
                        year,
                        start_date,
                        end_date,
                        start_half_day,
                        end_half_day,
                        requested_half_units,
                        status: RequestStatus::Draft,
                        rejection_reason: None,
                        archived_at_utc: None,
                        created_at_utc: event.timestamp,
                        updated_at_utc: event.timestamp,
                        aza_dates,
                    },
                );
                batch.status_events.push(MigratedStatusEvent {
                    event_id: event.event_id,
                    request_id: event.request_id,
                    previous_status: None,
                    new_status: RequestStatus::Draft,
                    occurred_at_utc: event.timestamp,
                    rejection_reason: None,
                });
            }
            kind => {
                let request = requests.get_mut(&event.request_id).ok_or_else(|| {
                    MigrationCommitError::InvalidBatch(format!(
                        "Statusereignis {} folgt auf kein Created-Ereignis.",
                        event.event_id
                    ))
                })?;
                let previous_status = request.status;
                let (new_status, rejection_reason) = match kind {
                    LegacyEventKind::Exported { pdf_path } => {
                        batch.exports.push(MigratedExportRecord {
                            export_id: event.event_id.clone(),
                            request_id: event.request_id.clone(),
                            file_path: pdf_path.into(),
                            content_sha256: None,
                            exported_at_utc: event.timestamp,
                        });
                        (RequestStatus::Exported, None)
                    }
                    LegacyEventKind::Approved => (RequestStatus::Approved, None),
                    LegacyEventKind::Rejected { reason } => (RequestStatus::Rejected, reason),
                    LegacyEventKind::Archived => {
                        request.archived_at_utc = Some(event.timestamp);
                        (RequestStatus::Archived, None)
                    }
                    LegacyEventKind::Deleted => (RequestStatus::Deleted, None),
                    LegacyEventKind::Created { .. } => {
                        return Err(MigrationCommitError::InvalidBatch(
                            "Ein Erstellungsereignis kann nicht als Statusänderung angewendet werden.".into(),
                        ));
                    }
                };
                request.status = new_status;
                request.updated_at_utc = event.timestamp;
                if new_status == RequestStatus::Rejected {
                    request.rejection_reason = rejection_reason.clone();
                }
                batch.status_events.push(MigratedStatusEvent {
                    event_id: event.event_id,
                    request_id: event.request_id,
                    previous_status: Some(previous_status),
                    new_status,
                    occurred_at_utc: event.timestamp,
                    rejection_reason,
                });
            }
        }
    }
    batch.requests.extend(requests.into_values());
    Ok(())
}

fn parse_status(value: &str) -> Result<RequestStatus, MigrationCommitError> {
    match value {
        "Draft" => Ok(RequestStatus::Draft),
        "Exported" => Ok(RequestStatus::Exported),
        "Approved" => Ok(RequestStatus::Approved),
        "Rejected" => Ok(RequestStatus::Rejected),
        "Archived" => Ok(RequestStatus::Archived),
        "Deleted" => Ok(RequestStatus::Deleted),
        _ => Err(MigrationCommitError::InvalidBatch(format!(
            "Unbekannter Status {value:?}."
        ))),
    }
}

fn parse_timestamp_text(value: &str) -> Result<DateTime<Utc>, MigrationCommitError> {
    DateTime::parse_from_rfc3339(value)
        .map(|value| value.to_utc())
        .map_err(|_| {
            MigrationCommitError::InvalidBatch(format!("Ungültiger Zeitstempel {value:?}."))
        })
}

fn parse_date_text(value: &str) -> Option<NaiveDate> {
    let date = value.get(..10).unwrap_or(value);
    NaiveDate::parse_from_str(date, "%Y-%m-%d").ok()
}

fn parse_bool_text(value: &str) -> Result<bool, MigrationCommitError> {
    match value {
        "true" | "True" => Ok(true),
        "false" | "False" => Ok(false),
        _ => Err(MigrationCommitError::InvalidBatch(format!(
            "Ungültiger Wahrheitswert {value:?}."
        ))),
    }
}

fn optional_timestamp(value: &str) -> Result<Option<DateTime<Utc>>, MigrationCommitError> {
    if value.trim().is_empty() {
        Ok(None)
    } else {
        parse_timestamp_text(value).map(Some)
    }
}

fn add_seconds(value: DateTime<Utc>, seconds: i64) -> Result<DateTime<Utc>, MigrationCommitError> {
    value
        .checked_add_signed(Duration::seconds(seconds))
        .ok_or_else(|| {
            MigrationCommitError::InvalidBatch(
                "Zeitstempel liegt außerhalb des darstellbaren Bereichs.".into(),
            )
        })
}

fn field(record: &StringRecord, index: usize) -> Result<&str, MigrationCommitError> {
    record
        .get(index)
        .ok_or_else(|| MigrationCommitError::InvalidBatch(format!("Feld {index} fehlt.")))
}

fn parse_u32(record: &StringRecord, index: usize) -> Result<u32, (IssueKind, String)> {
    field(record, index)
        .map_err(|error| (IssueKind::InvalidRecord, error.to_string()))?
        .parse()
        .map_err(|_| {
            (
                IssueKind::InvalidRecord,
                format!("Feld {index} enthält keine gültige Zahl."),
            )
        })
}

fn parse_i32(record: &StringRecord, index: usize) -> Result<i32, (IssueKind, String)> {
    field(record, index)
        .map_err(|error| (IssueKind::InvalidRecord, error.to_string()))?
        .parse()
        .map_err(|_| {
            (
                IssueKind::InvalidRecord,
                format!("Feld {index} enthält keine gültige Zahl."),
            )
        })
}

fn nonempty(value: &str) -> Result<String, MigrationCommitError> {
    let value = value.trim();
    if value.is_empty() {
        Err(MigrationCommitError::InvalidBatch(
            "Pflichttextfeld ist leer.".into(),
        ))
    } else {
        Ok(value.to_owned())
    }
}

fn nonempty_optional(value: &str) -> Option<String> {
    let value = value.trim();
    (!value.is_empty()).then(|| value.to_owned())
}

fn decode_utf8(bytes: &[u8]) -> Option<&str> {
    let bytes = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]).unwrap_or(bytes);
    std::str::from_utf8(bytes).ok()
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;
    use std::sync::atomic::{AtomicU64, Ordering};

    use chrono::DateTime;
    use urlaubstool_storage::Database;

    use super::*;
    use crate::preview::preview_file;

    static NEXT_TEMP_ID: AtomicU64 = AtomicU64::new(1);

    struct TempDirectory(PathBuf);

    impl TempDirectory {
        fn new() -> Self {
            let id = NEXT_TEMP_ID.fetch_add(1, Ordering::Relaxed);
            let path = std::env::temp_dir().join(format!(
                "urlaubstool-migration-commit-{}-{id}",
                std::process::id()
            ));
            fs::create_dir_all(&path).expect("create temporary test directory");
            Self(path)
        }

        fn file(&self, name: &str) -> PathBuf {
            self.0.join(name)
        }
    }

    impl Drop for TempDirectory {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn timestamp(value: &str) -> DateTime<Utc> {
        DateTime::parse_from_rfc3339(value)
            .expect("valid test timestamp")
            .to_utc()
    }

    #[test]
    fn req_mig_003_settings_preview_requires_backup_before_transactional_commit() {
        let directory = TempDirectory::new();
        let source = directory.file("settings.json");
        let backup = directory.file("settings.backup.json");
        let contents = br##"{
            "Version": 2,
            "Name": "Ada Beispiel",
            "Vorname": "Ada",
            "Nachname": "Beispiel",
            "Jahresurlaub": 30.5,
            "Workdays": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
            "Bundesland": "NW",
            "StudentActive": true,
            "VocationalSchool": {"Wednesday": "Half"},
            "ColorSettings": {"Normaltag": "#123456"}
        }"##;
        fs::write(&source, contents).expect("write settings source");
        let preview = preview_file(&source).expect("preview settings");
        let original = fs::read(&source).expect("read original settings");
        let mut database = Database::open_in_memory().expect("open database");

        let receipt = commit_preview(
            &mut database,
            &preview,
            &backup,
            timestamp("2026-11-10T10:00:00Z"),
            timestamp("2026-11-10T10:00:01Z"),
            false,
        )
        .expect("commit settings preview");

        assert!(receipt.settings_imported);
        assert_eq!(
            fs::read(&source).expect("source remains unchanged"),
            original
        );
        assert_eq!(fs::read(&backup).expect("backup exists"), original);
        let settings = database
            .load_settings()
            .expect("load imported settings")
            .expect("settings imported");
        assert_eq!(settings.first_name, "Ada");
        assert_eq!(settings.last_name, "Beispiel");
        assert_eq!(settings.annual_entitlement_half_units, 61);
        assert_eq!(settings.workday_mask, 0b0001_1111);
        assert_eq!(settings.vocational_school_week[2], 1);
        assert_eq!(
            settings
                .calendar_colors
                .get("Normaltag")
                .map(String::as_str),
            Some("#123456")
        );
    }

    #[test]
    fn req_mig_003_settings_rejects_mistyped_optional_values() {
        let error = parse_settings(r#"{"StudentActive":"false"}"#, 2)
            .expect_err("mistyped settings must not be silently discarded");

        assert!(error
            .to_string()
            .contains("StudentActive muss boolesch sein"));
    }

    #[test]
    fn req_mig_003_ledger_preview_commits_request_status_aza_and_export_atomically() {
        let directory = TempDirectory::new();
        let source = directory.file("ledger.csv");
        let backup = directory.file("ledger.backup.csv");
        let ledger = format!(
            "{}\n1,2026,550e8400-e29b-41d4-a716-446655440001,2026-01-02T03:04:05Z,2026-11-09,2026-11-10,true,false,1.5,Approved,,,/old/Urlaubsantrag.pdf\n",
            LEDGER_HEADER.join(",")
        );
        fs::write(&source, &ledger).expect("write ledger source");
        let preview = preview_file(&source).expect("preview ledger");
        let mut database = Database::open_in_memory().expect("open database");

        let receipt = commit_preview(
            &mut database,
            &preview,
            &backup,
            timestamp("2026-11-10T10:00:00Z"),
            timestamp("2026-11-10T10:00:01Z"),
            false,
        )
        .expect("commit ledger preview");

        assert_eq!(receipt.requests_imported, 1);
        assert_eq!(receipt.status_events_imported, 3);
        assert_eq!(receipt.exports_imported, 1);
        assert_eq!(
            fs::read(&source).expect("source remains"),
            ledger.as_bytes()
        );

        let repeated_backup = directory.file("ledger.second-backup.csv");
        let repeated = commit_preview(
            &mut database,
            &preview,
            repeated_backup,
            timestamp("2026-11-10T10:00:02Z"),
            timestamp("2026-11-10T10:00:03Z"),
            false,
        )
        .expect("repeat same migration");
        assert!(repeated.already_imported);
        assert_eq!(repeated.requests_imported, 0);
    }

    #[test]
    fn req_mig_003_history_jsonl_preserves_aza_and_status_timeline() {
        let directory = TempDirectory::new();
        let source = directory.file("history.jsonl");
        let backup = directory.file("history.backup.jsonl");
        let contents = concat!(
            "{\"$type\":\"Created\",\"EventId\":\"550e8400-e29b-41d4-a716-446655440010\",\"RequestId\":\"550e8400-e29b-41d4-a716-446655440011\",\"Timestamp\":\"2026-01-02T03:04:05Z\",\"Year\":2026,\"StartDate\":\"2026-11-09\",\"EndDate\":\"2026-11-10\",\"StartHalfDay\":true,\"EndHalfDay\":false,\"CalculatedDays\":1.5,\"AzaDates\":[\"2026-11-10\"]}\n",
            "{\"$type\":\"Exported\",\"EventId\":\"550e8400-e29b-41d4-a716-446655440012\",\"RequestId\":\"550e8400-e29b-41d4-a716-446655440011\",\"Timestamp\":\"2026-01-02T03:04:06Z\",\"PdfPath\":\"/old/Antrag.pdf\"}\n",
            "{\"$type\":\"Approved\",\"EventId\":\"550e8400-e29b-41d4-a716-446655440013\",\"RequestId\":\"550e8400-e29b-41d4-a716-446655440011\",\"Timestamp\":\"2026-01-02T03:04:07Z\"}\n"
        );
        fs::write(&source, contents).expect("write history source");
        let preview = preview_file(&source).expect("preview history");
        let mut database = Database::open_in_memory().expect("open database");

        let receipt = commit_preview(
            &mut database,
            &preview,
            &backup,
            timestamp("2026-11-10T10:00:00Z"),
            timestamp("2026-11-10T10:00:01Z"),
            false,
        )
        .expect("commit history preview");

        assert_eq!(receipt.requests_imported, 1);
        assert_eq!(receipt.status_events_imported, 3);
        assert_eq!(receipt.exports_imported, 1);
    }

    #[test]
    fn req_mig_003_history_csv_preserves_rejection_reason_and_export() {
        let directory = TempDirectory::new();
        let source = directory.file("history.csv");
        let backup = directory.file("history.backup.csv");
        let content = format!(
            "{}\n550e8400-e29b-41d4-a716-446655440020,550e8400-e29b-41d4-a716-446655440021,2026-01-02T03:04:05Z,Created,2026,2026-11-09,2026-11-10,true,false,1.5,,\n550e8400-e29b-41d4-a716-446655440022,550e8400-e29b-41d4-a716-446655440021,2026-01-02T03:04:06Z,Exported,,,,,,,/old/Antrag.pdf,\n550e8400-e29b-41d4-a716-446655440023,550e8400-e29b-41d4-a716-446655440021,2026-01-02T03:04:07Z,Rejected,,,,,,,,\"Grund, mit Komma\"\n",
            HISTORY_HEADER.join(",")
        );
        fs::write(&source, &content).expect("write history csv");
        let preview = preview_file(&source).expect("preview history csv");
        let mut database = Database::open_in_memory().expect("open database");

        let receipt = commit_preview(
            &mut database,
            &preview,
            &backup,
            timestamp("2026-11-10T10:00:00Z"),
            timestamp("2026-11-10T10:00:01Z"),
            false,
        )
        .expect("commit history csv");

        assert_eq!(receipt.requests_imported, 1);
        assert_eq!(receipt.status_events_imported, 3);
        assert_eq!(receipt.exports_imported, 1);
    }

    #[test]
    fn req_mig_003_skipped_history_event_requires_confirmation_before_backup_or_write() {
        let directory = TempDirectory::new();
        let source = directory.file("history.jsonl");
        let backup = directory.file("history.backup.jsonl");
        let content = concat!(
            "{\"$type\":\"Created\",\"EventId\":\"550e8400-e29b-41d4-a716-446655440030\",\"RequestId\":\"550e8400-e29b-41d4-a716-446655440031\",\"Timestamp\":\"2026-01-02T03:04:05Z\",\"Year\":2026,\"StartDate\":\"2026-11-09\",\"EndDate\":\"2026-11-09\",\"StartHalfDay\":false,\"EndHalfDay\":false,\"CalculatedDays\":1}\n",
            "{\"$type\":\"FutureEvent\"}\n"
        );
        fs::write(&source, content).expect("write history source");
        let preview = preview_file(&source).expect("preview history");
        let mut database = Database::open_in_memory().expect("open database");

        assert!(matches!(
            commit_preview(
                &mut database,
                &preview,
                &backup,
                timestamp("2026-11-10T10:00:00Z"),
                timestamp("2026-11-10T10:00:01Z"),
                false,
            ),
            Err(MigrationCommitError::SkippedRecordsNeedConfirmation)
        ));
        assert!(!backup.exists());

        let receipt = commit_preview(
            &mut database,
            &preview,
            &backup,
            timestamp("2026-11-10T10:00:00Z"),
            timestamp("2026-11-10T10:00:01Z"),
            true,
        )
        .expect("confirm skipped event and commit");
        assert_eq!(receipt.requests_imported, 1);
        assert!(backup.exists());
    }
}
