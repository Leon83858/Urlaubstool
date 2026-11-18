#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod diagnostics;

use std::path::PathBuf;
use std::sync::Mutex;

use serde::{Deserialize, Serialize};
use tauri::Manager;
use urlaubstool_api::{ApiError, CreateRequestInput, RequestDto, SettingsDto, StateDto};
use urlaubstool_application::{
    calculate_vacation as calculate_in_application, prepare_request_pdf, PdfPersonalDetails,
    StudentCalendar, VacationCalculationInput, VacationDayOutput,
};
use urlaubstool_storage::Database;

const DATABASE_FILE_NAME: &str = "urlaubstool.sqlite";

/// Bericht des letzten Altdaten-Imports dieser Sitzung (REQ-MIG-007), Pfade gekürzt.
static LAST_MIGRATION_REPORT: Mutex<Option<String>> = Mutex::new(None);

/// Lokale Datenbank der App. Schlägt das Öffnen fehl, startet die App trotzdem
/// und jeder Datenzugriff meldet die Ursache (REQ-DIAG-003).
struct AppDatabase(Mutex<Result<Database, String>>);

fn open_database(data_directory: Result<PathBuf, String>) -> Result<Database, String> {
    let directory = data_directory.map_err(|error| {
        format!("Der Datenordner der App konnte nicht ermittelt werden: {error}")
    })?;
    Database::open(directory.join(DATABASE_FILE_NAME)).map_err(|error| {
        format!("Die lokale Datenbank konnte nicht geöffnet werden. Es wurden keine Daten verändert. Ursache: {error}")
    })
}

fn with_database<T>(
    state: &AppDatabase,
    action: impl FnOnce(&mut Database) -> Result<T, ApiError>,
) -> Result<T, String> {
    let mut guard = state.0.lock().map_err(|_| {
        "Die Datenbank ist nach einem internen Fehler gesperrt. Bitte die App neu starten."
            .to_owned()
    })?;
    match guard.as_mut() {
        Ok(database) => action(database).map_err(|error| error.to_string()),
        Err(message) => Err(message.clone()),
    }
}

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct CalculationInput {
    start_date: String,
    end_date: String,
    start_half_day: bool,
    end_half_day: bool,
    workdays: Vec<bool>,
    federal_state: String,
    annual_entitlement_half_units: u32,
    already_approved_half_units: u32,
    #[serde(default)]
    aza_dates: Vec<String>,
    #[serde(default)]
    include_optional_holidays: bool,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct PdfPersonalInput {
    first_name: String,
    last_name: String,
    #[serde(default)]
    address: String,
    department: String,
    #[serde(default)]
    personnel_number: String,
    class_name: String,
    /// Antragsdatum (lokales Datum der Oberfläche, JJJJ-MM-TT).
    application_date: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct CalculationOutput {
    requested_half_units: u32,
    annual_entitlement_half_units: u32,
    already_approved_half_units: u32,
    remaining_before_half_units: i64,
    remaining_after_half_units: i64,
    errors: Vec<String>,
    warnings: Vec<String>,
    days: Vec<VacationDayOutput>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct PdfExportOutput {
    path: String,
    requested_half_units: u32,
}

fn to_application_input(
    input: CalculationInput,
    student: StudentCalendar,
) -> VacationCalculationInput {
    VacationCalculationInput {
        start_date: input.start_date,
        end_date: input.end_date,
        start_half_day: input.start_half_day,
        end_half_day: input.end_half_day,
        workdays: input.workdays,
        federal_state: input.federal_state,
        annual_entitlement_half_units: input.annual_entitlement_half_units,
        already_approved_half_units: input.already_approved_half_units,
        aza_dates: input.aza_dates,
        include_optional_holidays: input.include_optional_holidays,
        student,
    }
}

fn export_pdf_at_path(
    input: VacationCalculationInput,
    personal: PdfPersonalDetails,
    application_date: chrono::NaiveDate,
    path: &std::path::Path,
) -> Result<PdfExportOutput, String> {
    let path_text = path
        .to_str()
        .ok_or("Der ausgewählte Dateipfad kann nicht dargestellt werden.")?
        .to_owned();
    let data = prepare_request_pdf(input, personal, application_date)
        .map_err(|error| error.to_string())?;
    urlaubstool_storage::export_pdf_to_new(&data, path).map_err(|error| error.to_string())?;
    Ok(PdfExportOutput {
        path: path_text,
        requested_half_units: data.requested_half_units,
    })
}

/// Berufsschule und importierte Schulferien aus der lokalen Datenbank (REQ-CALC-006/007).
#[cfg(test)]
fn student_calendar_for(
    state: &AppDatabase,
    federal_state: &str,
) -> Result<StudentCalendar, String> {
    with_database(state, |database| {
        urlaubstool_api::student_calendar(database, federal_state)
    })
}

#[tauri::command]
fn calculate_vacation(
    state: tauri::State<'_, AppDatabase>,
    input: CalculationInput,
) -> Result<CalculationOutput, String> {
    let application_input = stored_context_input(&state, input)?;
    calculate_application_input(application_input)
}

/// Berechnungsgrundlagen kommen aus der Datenbank, nicht aus der Oberfläche (REQ-ARCH-002).
fn stored_context_input(
    state: &AppDatabase,
    input: CalculationInput,
) -> Result<VacationCalculationInput, String> {
    let mut application_input = to_application_input(input, StudentCalendar::default());
    with_database(state, |database| {
        urlaubstool_api::apply_stored_context(database, &mut application_input)
    })?;
    Ok(application_input)
}

#[cfg(test)]
fn calculate_with_student(
    input: CalculationInput,
    student: StudentCalendar,
) -> Result<CalculationOutput, String> {
    calculate_application_input(to_application_input(input, student))
}

fn calculate_application_input(
    input: VacationCalculationInput,
) -> Result<CalculationOutput, String> {
    let result = calculate_in_application(input)?;
    Ok(CalculationOutput {
        requested_half_units: result.requested_half_units,
        annual_entitlement_half_units: result.annual_entitlement_half_units,
        already_approved_half_units: result.already_approved_half_units,
        remaining_before_half_units: result.remaining_before_half_units,
        remaining_after_half_units: result.remaining_after_half_units,
        errors: result.errors,
        warnings: result.warnings,
        days: result.days,
    })
}

#[tauri::command]
async fn export_vacation_pdf(
    state: tauri::State<'_, AppDatabase>,
    input: CalculationInput,
    personal: PdfPersonalInput,
) -> Result<Option<PdfExportOutput>, String> {
    let file_name = format!(
        "Urlaubsantrag_{}_bis_{}.pdf",
        input.start_date, input.end_date
    );
    let input = stored_context_input(&state, input)?;
    let application_date =
        chrono::NaiveDate::parse_from_str(&personal.application_date, "%Y-%m-%d")
            .map_err(|_| "Das Antragsdatum ist ungültig.".to_owned())?;
    let optional = |value: String| (!value.trim().is_empty()).then_some(value);
    let personal = PdfPersonalDetails {
        first_name: personal.first_name,
        last_name: personal.last_name,
        address: optional(personal.address),
        department: optional(personal.department),
        personnel_number: optional(personal.personnel_number),
        class_name: optional(personal.class_name),
    };
    prepare_request_pdf(input.clone(), personal.clone(), application_date)
        .map_err(|error| error.to_string())?;
    let selected = rfd::AsyncFileDialog::new()
        .set_title("Urlaubsantrag als PDF speichern")
        .add_filter("PDF", &["pdf"])
        .set_file_name(file_name)
        .save_file()
        .await;
    let Some(file) = selected else {
        return Ok(None);
    };
    let path = file.path().to_path_buf();
    let result = tauri::async_runtime::spawn_blocking(move || {
        export_pdf_at_path(input, personal, application_date, &path)
    })
    .await
    .map_err(|error| format!("PDF-Export konnte nicht abgeschlossen werden: {error}"))??;
    Ok(Some(result))
}

#[tauri::command]
fn load_state(state: tauri::State<'_, AppDatabase>) -> Result<StateDto, String> {
    with_database(&state, |database| urlaubstool_api::load_state(database))
}

#[tauri::command]
fn save_settings(
    state: tauri::State<'_, AppDatabase>,
    settings: SettingsDto,
) -> Result<SettingsDto, String> {
    with_database(&state, |database| {
        urlaubstool_api::save_settings(database, settings)
    })
}

#[tauri::command]
fn create_request(
    state: tauri::State<'_, AppDatabase>,
    input: CreateRequestInput,
) -> Result<RequestDto, String> {
    with_database(&state, |database| {
        urlaubstool_api::create_request(database, input, chrono::Utc::now())
    })
}

#[tauri::command]
fn change_request_status(
    state: tauri::State<'_, AppDatabase>,
    request_id: String,
    status: String,
    rejection_reason: Option<String>,
) -> Result<RequestDto, String> {
    with_database(&state, |database| {
        urlaubstool_api::change_request_status(
            database,
            &request_id,
            &status,
            rejection_reason.as_deref(),
            chrono::Utc::now(),
        )
    })
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct RestoreOutput {
    state: StateDto,
    /// Sicherung des Stands vor der Wiederherstellung.
    safety_backup: String,
}

fn parse_local_date(value: &str, what: &str) -> Result<chrono::NaiveDate, String> {
    chrono::NaiveDate::parse_from_str(value, "%Y-%m-%d")
        .map_err(|_| format!("{what} ist ungültig."))
}

fn path_text(path: &std::path::Path) -> Result<String, String> {
    path.to_str()
        .map(str::to_owned)
        .ok_or_else(|| "Der ausgewählte Dateipfad kann nicht dargestellt werden.".to_owned())
}

/// Schreibt das PDF eines gespeicherten Antrags und vermerkt den Export (REQ-HIST-006).
fn export_stored_request_at_path(
    state: &AppDatabase,
    request_id: &str,
    data: urlaubstool_application::PdfRequestData,
    path: &std::path::Path,
) -> Result<RequestDto, String> {
    let shown = path_text(path)?;
    urlaubstool_storage::export_pdf_to_new(&data, path).map_err(|error| error.to_string())?;
    with_database(state, |database| {
        urlaubstool_api::record_request_export(database, request_id, path, chrono::Utc::now())
    })
    .map_err(|error| {
        format!("Das PDF wurde unter {shown} gespeichert, aber der Export konnte nicht am Antrag vermerkt werden: {error}")
    })
}

/// Stellt eine Sicherung wieder her; der vorherige Stand wird im Ordner
/// „Sicherungen“ neben der Datenbank abgelegt (REQ-DATA-004).
fn restore_backup_from_path(
    state: &AppDatabase,
    backup: &std::path::Path,
    now: chrono::DateTime<chrono::Utc>,
) -> Result<RestoreOutput, String> {
    with_database(state, |database| {
        let directory = database
            .path()
            .and_then(std::path::Path::parent)
            .map(|parent| parent.join("Sicherungen"))
            .ok_or_else(|| ApiError::Invalid("Der Datenordner der App ist unbekannt.".into()))?;
        let safety = directory.join(format!(
            "vor-Wiederherstellung_{}.sqlite",
            now.format("%Y-%m-%d_%H-%M-%S")
        ));
        let state = urlaubstool_api::restore_database(database, backup, &safety)?;
        Ok(RestoreOutput {
            state,
            safety_backup: safety.to_string_lossy().into_owned(),
        })
    })
}

#[tauri::command]
async fn export_request_pdf(
    state: tauri::State<'_, AppDatabase>,
    request_id: String,
    application_date: String,
) -> Result<Option<RequestDto>, String> {
    let application_date = parse_local_date(&application_date, "Das Antragsdatum")?;
    let data = with_database(&state, |database| {
        urlaubstool_api::prepare_stored_request_pdf(database, &request_id, application_date)
    })?;
    let selected = rfd::AsyncFileDialog::new()
        .set_title("Urlaubsantrag als PDF speichern")
        .add_filter("PDF", &["pdf"])
        .set_file_name(format!(
            "Urlaubsantrag_{}_bis_{}.pdf",
            data.start_date, data.end_date
        ))
        .save_file()
        .await;
    let Some(file) = selected else {
        return Ok(None);
    };
    export_stored_request_at_path(&state, &request_id, data, file.path()).map(Some)
}

#[tauri::command]
async fn create_backup(
    state: tauri::State<'_, AppDatabase>,
    today: String,
) -> Result<Option<String>, String> {
    let today = parse_local_date(&today, "Das Datum")?;
    let selected = rfd::AsyncFileDialog::new()
        .set_title("Sicherung speichern")
        .add_filter("Urlaubstool-Sicherung", &["sqlite"])
        .set_file_name(format!("Urlaubstool-Sicherung_{today}.sqlite"))
        .save_file()
        .await;
    let Some(file) = selected else {
        return Ok(None);
    };
    let path = file.path().to_path_buf();
    let shown = path_text(&path)?;
    with_database(&state, |database| {
        urlaubstool_api::backup_database(database, &path)
    })?;
    Ok(Some(shown))
}

#[tauri::command]
async fn restore_backup(
    state: tauri::State<'_, AppDatabase>,
) -> Result<Option<RestoreOutput>, String> {
    let selected = rfd::AsyncFileDialog::new()
        .set_title("Sicherung wiederherstellen")
        .add_filter("Urlaubstool-Sicherung", &["sqlite"])
        .pick_file()
        .await;
    let Some(file) = selected else {
        return Ok(None);
    };
    restore_backup_from_path(&state, file.path(), chrono::Utc::now()).map(Some)
}

#[tauri::command]
fn calendar_year(
    state: tauri::State<'_, AppDatabase>,
    year: i32,
) -> Result<Vec<urlaubstool_api::CalendarDayOutput>, String> {
    with_database(&state, |database| {
        urlaubstool_api::calendar_year(database, year)
    })
}

/// Summen und Anträge eines Jahres für die Übersicht (REQ-ARCH-002, REQ-UI-005).
#[tauri::command]
fn year_summary(
    state: tauri::State<'_, AppDatabase>,
    year: i32,
) -> Result<urlaubstool_api::YearSummaryDto, String> {
    with_database(&state, |database| {
        urlaubstool_api::year_summary(database, year)
    })
}

/// Liest eine KMK-Ferienkalenderdatei (ICS) ein (REQ-HOL-003, E2).
fn import_school_holidays_from_path(
    state: &AppDatabase,
    path: &std::path::Path,
    now: chrono::DateTime<chrono::Utc>,
) -> Result<urlaubstool_api::CalendarStatusDto, String> {
    let size = std::fs::metadata(path)
        .map_err(|error| format!("Die Kalenderdatei konnte nicht gelesen werden: {error}"))?
        .len();
    if size > urlaubstool_api::MAX_ICS_BYTES as u64 {
        return Err(
            "Die Kalenderdatei ist zu groß für einen Ferienkalender. Es wurde nichts verändert."
                .into(),
        );
    }
    let contents = std::fs::read(path)
        .map_err(|error| format!("Die Kalenderdatei konnte nicht gelesen werden: {error}"))?;
    let file_name = path
        .file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_default();
    with_database(state, |database| {
        urlaubstool_api::import_school_holidays(database, &contents, &file_name, now)
    })
}

#[tauri::command]
async fn import_school_holidays(
    state: tauri::State<'_, AppDatabase>,
) -> Result<Option<urlaubstool_api::CalendarStatusDto>, String> {
    let selected = rfd::AsyncFileDialog::new()
        .set_title("Ferienkalender (ICS) der KMK importieren")
        .add_filter("Kalender", &["ics"])
        .pick_file()
        .await;
    let Some(file) = selected else {
        return Ok(None);
    };
    import_school_holidays_from_path(&state, file.path(), chrono::Utc::now()).map(Some)
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct MigrationSelection {
    path: String,
    preview: urlaubstool_api::MigrationPreviewDto,
}

fn backup_directory(database: &urlaubstool_storage::Database) -> Result<PathBuf, ApiError> {
    database
        .path()
        .and_then(std::path::Path::parent)
        .map(|parent| parent.join("Sicherungen"))
        .ok_or_else(|| ApiError::Invalid("Der Datenordner der App ist unbekannt.".into()))
}

fn preview_migration_path(
    state: &AppDatabase,
    path: &std::path::Path,
) -> Result<MigrationSelection, String> {
    let preview = with_database(state, |database| {
        urlaubstool_api::preview_legacy_file(database, path)
    })?;
    Ok(MigrationSelection {
        path: path_text(path)?,
        preview,
    })
}

fn import_migration_path(
    state: &AppDatabase,
    path: &std::path::Path,
    source_sha256: &str,
    confirm_skipped: bool,
    now: chrono::DateTime<chrono::Utc>,
) -> Result<urlaubstool_api::MigrationResultDto, String> {
    let result = with_database(state, |database| {
        let directory = backup_directory(database)?;
        urlaubstool_api::import_legacy_file(
            database,
            path,
            source_sha256,
            confirm_skipped,
            &directory,
            now,
        )
    });
    match &result {
        Ok(done) => {
            if let Ok(mut report) = LAST_MIGRATION_REPORT.lock() {
                *report = Some(redacted(&urlaubstool_api::migration_report(done, now)));
            }
            diagnostics::record(
                diagnostics::Level::Info,
                "migration",
                &format!(
                    "{}: {} Anträge, {} Statusereignisse, {} Exporte, Einstellungen {}",
                    done.file_name,
                    done.requests_imported,
                    done.status_events_imported,
                    done.exports_imported,
                    if done.settings_imported { "ja" } else { "nein" }
                ),
            );
        }
        Err(message) => diagnostics::record(diagnostics::Level::Error, "migration", message),
    }
    result
}

/// Öffnet eine Altdatei der bisherigen Anwendung nur lesend und zeigt die Vorschau.
#[tauri::command]
async fn preview_migration(
    state: tauri::State<'_, AppDatabase>,
) -> Result<Option<MigrationSelection>, String> {
    let selected = rfd::AsyncFileDialog::new()
        .set_title("Altdaten auswählen (settings.json, ledger.csv, history.csv, history.jsonl)")
        .add_filter("Urlaubstool-Altdaten", &["json", "csv", "jsonl"])
        .pick_file()
        .await;
    let Some(file) = selected else {
        return Ok(None);
    };
    preview_migration_path(&state, file.path()).map(Some)
}

#[tauri::command]
fn import_migration(
    state: tauri::State<'_, AppDatabase>,
    path: String,
    source_sha256: String,
    confirm_skipped: bool,
) -> Result<urlaubstool_api::MigrationResultDto, String> {
    import_migration_path(
        &state,
        std::path::Path::new(&path),
        &source_sha256,
        confirm_skipped,
        chrono::Utc::now(),
    )
}

/// Nimmt eine Fehlermeldung der Oberfläche ins Diagnoseprotokoll auf (REQ-DIAG-001).
#[tauri::command]
fn record_error(context: String, message: String) {
    diagnostics::record(diagnostics::Level::Error, &context, &message);
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct DiagnosticsInfo {
    version: String,
    os: String,
    arch: String,
    log_file: String,
    database_file: String,
    database_status: String,
    recent_errors: Vec<diagnostics::LogEntry>,
}

fn redacted(text: &str) -> String {
    diagnostics::global().map_or_else(|| text.to_owned(), |log| log.redact(text))
}

fn collect_diagnostics(state: &AppDatabase) -> DiagnosticsInfo {
    let (database_file, database_status) = match state.0.lock() {
        Ok(guard) => match guard.as_ref() {
            Ok(database) => (
                database
                    .path()
                    .map(|path| redacted(&path.to_string_lossy()))
                    .unwrap_or_else(|| "im Arbeitsspeicher".into()),
                "Geöffnet".to_owned(),
            ),
            Err(message) => ("nicht verfügbar".into(), redacted(message)),
        },
        Err(_) => (
            "nicht verfügbar".into(),
            "Gesperrt nach internem Fehler".into(),
        ),
    };
    DiagnosticsInfo {
        version: env!("CARGO_PKG_VERSION").into(),
        os: std::env::consts::OS.into(),
        arch: std::env::consts::ARCH.into(),
        log_file: diagnostics::global()
            .map(|log| redacted(&log.path().to_string_lossy()))
            .unwrap_or_else(|| "nicht eingerichtet".into()),
        database_file,
        database_status,
        recent_errors: diagnostics::global()
            .map(|log| log.recent(20, diagnostics::Level::Warn))
            .unwrap_or_default(),
    }
}

/// Textbericht für den Diagnoseexport; enthält keine Antrags- oder Personendaten.
fn diagnostics_report(info: &DiagnosticsInfo, created: chrono::DateTime<chrono::Utc>) -> String {
    let mut text = format!(
        "Urlaubstool Diagnosebericht\nErstellt (UTC): {}\nVersion: {}\nSystem: {} {}\nDatenbank: {} ({})\nProtokoll: {}\n\nLetzte Warnungen und Fehler ({}):\n",
        created.format("%Y-%m-%d %H:%M:%S"),
        info.version,
        info.os,
        info.arch,
        info.database_file,
        info.database_status,
        info.log_file,
        info.recent_errors.len()
    );
    for entry in &info.recent_errors {
        text.push_str(&format!(
            "- {} [{:?}] {}: {}\n",
            entry.time_utc, entry.level, entry.context, entry.message
        ));
    }
    if info.recent_errors.is_empty() {
        text.push_str("- keine\n");
    }
    text.push_str("\nHinweis: Der Bericht enthält keine Namen, Adressen oder Antragsdaten.\n");
    text
}

#[tauri::command]
fn diagnostics_info(state: tauri::State<'_, AppDatabase>) -> DiagnosticsInfo {
    collect_diagnostics(&state)
}

#[tauri::command]
async fn export_diagnostics(
    state: tauri::State<'_, AppDatabase>,
    today: String,
) -> Result<Option<String>, String> {
    let today = parse_local_date(&today, "Das Datum")?;
    let selected = rfd::AsyncFileDialog::new()
        .set_title("Diagnosebericht speichern")
        .add_filter("Text", &["txt"])
        .set_file_name(format!("Urlaubstool-Diagnose_{today}.txt"))
        .save_file()
        .await;
    let Some(file) = selected else {
        return Ok(None);
    };
    let report = diagnostics_report(&collect_diagnostics(&state), chrono::Utc::now());
    let path = file.path().to_path_buf();
    let shown = path_text(&path)?;
    std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&path)
        .and_then(|mut handle| std::io::Write::write_all(&mut handle, report.as_bytes()))
        .map_err(|error| format!("Der Diagnosebericht konnte nicht gespeichert werden: {error}"))?;
    Ok(Some(shown))
}

/// Speichert den Bericht des letzten Altdaten-Imports als Textdatei (REQ-MIG-007).
#[tauri::command]
async fn export_migration_report(today: String) -> Result<Option<String>, String> {
    let today = parse_local_date(&today, "Das Datum")?;
    let report = LAST_MIGRATION_REPORT
        .lock()
        .map_err(|_| "Der Importbericht ist nicht verfügbar.".to_owned())?
        .clone()
        .ok_or_else(|| "In dieser Sitzung wurde noch nichts importiert.".to_owned())?;
    let selected = rfd::AsyncFileDialog::new()
        .set_title("Importbericht speichern")
        .add_filter("Text", &["txt"])
        .set_file_name(format!("Urlaubstool-Importbericht_{today}.txt"))
        .save_file()
        .await;
    let Some(file) = selected else {
        return Ok(None);
    };
    let path = file.path().to_path_buf();
    let shown = path_text(&path)?;
    std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&path)
        .and_then(|mut handle| std::io::Write::write_all(&mut handle, report.as_bytes()))
        .map_err(|error| format!("Der Importbericht konnte nicht gespeichert werden: {error}"))?;
    Ok(Some(shown))
}

#[tauri::command]
fn app_version() -> &'static str {
    env!("CARGO_PKG_VERSION")
}

fn main() {
    let result = tauri::Builder::default()
        .setup(|app| {
            let data_directory = app.path().app_data_dir().map_err(|error| error.to_string());
            let log_directory = app
                .path()
                .app_log_dir()
                .ok()
                .or_else(|| data_directory.as_ref().ok().map(|dir| dir.join("Logs")));
            if let Some(directory) = log_directory {
                let home = std::env::var("HOME")
                    .or_else(|_| std::env::var("USERPROFILE"))
                    .ok();
                diagnostics::install(diagnostics::DiagnosticLog::new(
                    &directory,
                    home,
                    diagnostics::DEFAULT_MAX_BYTES,
                ));
            }
            let database = open_database(data_directory);
            match &database {
                Ok(_) => diagnostics::record(
                    diagnostics::Level::Info,
                    "start",
                    concat!("Version ", env!("CARGO_PKG_VERSION"), " gestartet"),
                ),
                Err(message) => diagnostics::record(diagnostics::Level::Error, "start", message),
            }
            app.manage(AppDatabase(Mutex::new(database)));
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            app_version,
            calculate_vacation,
            export_vacation_pdf,
            load_state,
            save_settings,
            create_request,
            change_request_status,
            export_request_pdf,
            create_backup,
            restore_backup,
            import_school_holidays,
            calendar_year,
            year_summary,
            record_error,
            diagnostics_info,
            export_diagnostics,
            preview_migration,
            import_migration,
            export_migration_report
        ])
        .run(tauri::generate_context!());

    if let Err(error) = result {
        diagnostics::record(diagnostics::Level::Error, "start", &error.to_string());
        eprintln!("Urlaubstool konnte nicht gestartet werden: {error}");
        std::process::exit(1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn input(start: &str, end: &str) -> CalculationInput {
        CalculationInput {
            start_date: start.into(),
            end_date: end.into(),
            start_half_day: false,
            end_half_day: false,
            workdays: vec![true, true, true, true, true, false, false],
            federal_state: "NW".into(),
            annual_entitlement_half_units: 60,
            already_approved_half_units: 0,
            aza_dates: vec![],
            include_optional_holidays: false,
        }
    }

    #[test]
    fn calculates_real_holidays_and_workdays() {
        assert_eq!(
            calculate_with_student(input("2026-04-03", "2026-04-03"), Default::default())
                .unwrap()
                .requested_half_units,
            0
        );
        assert_eq!(
            calculate_with_student(input("2026-03-09", "2026-03-09"), Default::default())
                .unwrap()
                .requested_half_units,
            2
        );
        let mut saturday = input("2026-03-21", "2026-03-21");
        saturday.workdays[5] = true;
        assert_eq!(
            calculate_with_student(saturday, Default::default())
                .unwrap()
                .requested_half_units,
            2
        );
    }

    #[test]
    fn rejects_invalid_periods_and_excess_leave() {
        assert!(
            calculate_with_student(input("2026-03-20", "2026-03-16"), Default::default()).is_err()
        );
        assert!(
            calculate_with_student(input("2026-12-31", "2027-01-04"), Default::default()).is_err()
        );
        let mut too_much = input("2026-03-16", "2026-03-20");
        too_much.annual_entitlement_half_units = 2;
        assert!(!calculate_with_student(too_much, Default::default())
            .unwrap()
            .errors
            .is_empty());
    }

    #[test]
    fn req_dom_002_ipc_uses_integer_half_units() {
        let valid = r#"{"startDate":"2026-03-16","endDate":"2026-03-16","startHalfDay":true,"endHalfDay":false,"workdays":[true,true,true,true,true,false,false],"federalState":"NW","annualEntitlementHalfUnits":60,"alreadyApprovedHalfUnits":0}"#;
        let input: CalculationInput =
            serde_json::from_str(valid).expect("integer half units accepted");
        let output = calculate_with_student(input, Default::default()).unwrap();
        let json = serde_json::to_value(output).unwrap();
        assert_eq!(json["requestedHalfUnits"], 1);
        assert!(serde_json::from_str::<CalculationInput>(&valid.replace(
            "\"annualEntitlementHalfUnits\":60",
            "\"annualEntitlementHalfUnits\":30.5"
        ))
        .is_err());
    }

    #[test]
    fn req_calc_010_ipc_includes_day_details() {
        let output =
            calculate_with_student(input("2026-04-03", "2026-04-03"), Default::default()).unwrap();
        let json = serde_json::to_value(output).unwrap();
        assert_eq!(json["days"][0]["date"], "2026-04-03");
        assert_eq!(json["days"][0]["publicHoliday"], "Gesetzlich");
        assert_eq!(json["days"][0]["countedHalfUnits"], 0);
        assert!(json["days"][0]["isSchoolVacation"].is_null());
    }

    #[test]
    fn req_aza_001_ipc_transmits_aza_dates() {
        let mut request = input("2026-03-16", "2026-03-20");
        request.aza_dates = vec!["2026-03-18".into()];
        let output = calculate_with_student(request, Default::default()).unwrap();
        assert_eq!(output.requested_half_units, 8);
        assert!(output.days[2].is_aza);
    }

    #[test]
    fn req_calc_011_ipc_transmits_remaining_balance() {
        let mut request = input("2026-03-16", "2026-03-16");
        request.annual_entitlement_half_units = 10;
        request.already_approved_half_units = 3;
        let json =
            serde_json::to_value(calculate_with_student(request, Default::default()).unwrap())
                .unwrap();
        assert_eq!(json["annualEntitlementHalfUnits"], 10);
        assert_eq!(json["alreadyApprovedHalfUnits"], 3);
        assert_eq!(json["remainingBeforeHalfUnits"], 7);
        assert_eq!(json["remainingAfterHalfUnits"], 5);
    }

    #[test]
    fn req_calc_003_ipc_transmits_optional_holiday_setting() {
        // E8: optional ist nur ein regionaler Feiertag (Mariä Himmelfahrt BY).
        let mut request = input("2025-08-15", "2025-08-15");
        request.federal_state = "BY".into();
        request.include_optional_holidays = true;
        let output = calculate_with_student(request, Default::default()).unwrap();
        assert!(output.days[0]
            .reasons
            .iter()
            .any(|reason| reason.contains("aktiviert")));
    }

    #[test]
    fn req_pdf_003_desktop_export_recalculates_and_never_overwrites() {
        let unique = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        // Zeitstempel allein sind bei parallel laufenden Tests nicht eindeutig.
        static NEXT_TEST_DIRECTORY: std::sync::atomic::AtomicU64 =
            std::sync::atomic::AtomicU64::new(0);
        let unique = format!(
            "{unique}-{}",
            NEXT_TEST_DIRECTORY.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
        );
        let directory = std::env::temp_dir().join(format!(
            "urlaubstool-tauri-pdf-{}-{unique}",
            std::process::id()
        ));
        std::fs::create_dir(&directory).unwrap();
        let path = directory.join("antrag.pdf");
        let personal = || urlaubstool_application::PdfPersonalDetails {
            first_name: "Änne".into(),
            last_name: "Müller".into(),
            address: Some("Musterweg 1, 12345 Ort".into()),
            department: None,
            personnel_number: None,
            class_name: None,
        };
        let today = chrono::NaiveDate::from_ymd_opt(2026, 3, 1).unwrap();
        let mut request = input("2026-03-16", "2026-03-20");
        request.aza_dates = vec!["2026-03-18".into()];
        let result = export_pdf_at_path(
            to_application_input(request, Default::default()),
            personal(),
            today,
            &path,
        )
        .unwrap();
        assert_eq!(result.requested_half_units, 8);
        let original = std::fs::read(&path).unwrap();
        assert!(original.starts_with(b"%PDF-"));
        assert!(export_pdf_at_path(
            to_application_input(input("2026-03-16", "2026-03-20"), Default::default()),
            personal(),
            today,
            &path
        )
        .is_err());
        assert_eq!(std::fs::read(&path).unwrap(), original);
        let invalid = directory.join("invalid.pdf");
        assert!(export_pdf_at_path(
            to_application_input(input("2026-12-31", "2027-01-04"), Default::default()),
            personal(),
            today,
            &invalid
        )
        .is_err());
        assert!(!invalid.exists());
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn req_diag_003_unavailable_database_is_reported_by_every_data_command() {
        let state = AppDatabase(Mutex::new(open_database(Err("kein Ordner".into()))));
        let message =
            with_database(&state, |database| urlaubstool_api::load_state(database)).unwrap_err();
        assert!(message.contains("Datenordner") && message.contains("kein Ordner"));
    }

    #[test]
    fn req_data_002_database_is_created_in_the_data_directory_and_reopened() {
        let unique = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        // Zeitstempel allein sind bei parallel laufenden Tests nicht eindeutig.
        static NEXT_TEST_DIRECTORY: std::sync::atomic::AtomicU64 =
            std::sync::atomic::AtomicU64::new(0);
        let unique = format!(
            "{unique}-{}",
            NEXT_TEST_DIRECTORY.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
        );
        let directory = std::env::temp_dir().join(format!(
            "urlaubstool-tauri-db-{}-{unique}",
            std::process::id()
        ));
        let settings = SettingsDto {
            first_name: "Änne".into(),
            last_name: "Müller".into(),
            address: String::new(),
            department: String::new(),
            personnel_number: String::new(),
            class_name: String::new(),
            annual_entitlement_half_units: 60,
            federal_state: "Nordrhein-Westfalen".into(),
            workdays: vec![true, true, true, true, true, false, false],
            include_optional_holidays: false,
            student_mode: false,
            vocational_school_week: vec![0; 7],
        };
        {
            let state = AppDatabase(Mutex::new(open_database(Ok(directory.clone()))));
            with_database(&state, |database| {
                urlaubstool_api::save_settings(database, settings.clone())
            })
            .unwrap();
        }
        assert!(directory.join(DATABASE_FILE_NAME).is_file());
        let state = AppDatabase(Mutex::new(open_database(Ok(directory.clone()))));
        let loaded =
            with_database(&state, |database| urlaubstool_api::load_state(database)).unwrap();
        assert_eq!(loaded.settings, Some(settings));
        drop(state);
        std::fs::remove_dir_all(directory).unwrap();
    }

    fn temporary_directory(label: &str) -> std::path::PathBuf {
        static NEXT: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
        let directory = std::env::temp_dir().join(format!(
            "urlaubstool-tauri-{label}-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
        ));
        let _ = std::fs::remove_dir_all(&directory);
        std::fs::create_dir_all(&directory).unwrap();
        directory
    }

    fn configured_state(directory: &std::path::Path) -> AppDatabase {
        let state = AppDatabase(Mutex::new(open_database(Ok(directory.to_path_buf()))));
        with_database(&state, |database| {
            urlaubstool_api::save_settings(
                database,
                SettingsDto {
                    first_name: "Änne".into(),
                    last_name: "Müller".into(),
                    address: String::new(),
                    department: String::new(),
                    personnel_number: String::new(),
                    class_name: String::new(),
                    annual_entitlement_half_units: 60,
                    federal_state: "Nordrhein-Westfalen".into(),
                    workdays: vec![true, true, true, true, true, false, false],
                    include_optional_holidays: false,
                    student_mode: false,
                    vocational_school_week: vec![0; 7],
                },
            )?;
            urlaubstool_api::create_request(
                database,
                CreateRequestInput {
                    request_id: "a".into(),
                    start_date: "2026-03-16".into(),
                    end_date: "2026-03-20".into(),
                    start_half_day: false,
                    end_half_day: false,
                    aza_dates: vec![],
                    expected_half_units: 10,
                    export_path: None,
                },
                chrono::Utc::now(),
            )
        })
        .unwrap();
        state
    }

    #[test]
    fn req_hist_006_history_export_writes_the_pdf_and_marks_the_request() {
        let directory = temporary_directory("history-export");
        let state = configured_state(&directory);
        let date = parse_local_date("2026-03-02", "Datum").unwrap();
        let data = with_database(&state, |database| {
            urlaubstool_api::prepare_stored_request_pdf(database, "a", date)
        })
        .unwrap();
        let path = directory.join("antrag.pdf");
        let updated = export_stored_request_at_path(&state, "a", data.clone(), &path).unwrap();
        assert_eq!(updated.status, "Exportiert");
        assert_eq!(updated.export_path.as_deref(), path.to_str());
        assert!(std::fs::read(&path).unwrap().starts_with(b"%PDF-"));
        // Eine vorhandene Datei wird nie überschrieben und nichts wird vermerkt.
        let before = std::fs::read(&path).unwrap();
        assert!(export_stored_request_at_path(&state, "a", data, &path).is_err());
        assert_eq!(std::fs::read(&path).unwrap(), before);
        let state_now =
            with_database(&state, |database| urlaubstool_api::load_state(database)).unwrap();
        assert_eq!(state_now.requests[0].status_events.len(), 2);
        drop(state);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn req_data_004_restore_puts_the_previous_state_into_the_backup_folder() {
        let directory = temporary_directory("restore");
        let state = configured_state(&directory);
        let backup = directory.join("Sicherung.sqlite");
        with_database(&state, |database| {
            urlaubstool_api::backup_database(database, &backup)
        })
        .unwrap();
        with_database(&state, |database| {
            urlaubstool_api::change_request_status(
                database,
                "a",
                "Gelöscht",
                None,
                chrono::Utc::now(),
            )
        })
        .unwrap();
        let now = chrono::DateTime::parse_from_rfc3339("2026-03-05T12:30:00Z")
            .unwrap()
            .with_timezone(&chrono::Utc);
        let restored = restore_backup_from_path(&state, &backup, now).unwrap();
        assert_eq!(restored.state.requests.len(), 1);
        assert!(restored.state.deleted_requests.is_empty());
        let safety = directory
            .join("Sicherungen")
            .join("vor-Wiederherstellung_2026-03-05_12-30-00.sqlite");
        assert_eq!(restored.safety_backup, safety.to_string_lossy());
        assert!(safety.is_file());
        // Zweite Wiederherstellung in derselben Sekunde überschreibt die Kopie nicht.
        assert!(restore_backup_from_path(&state, &backup, now).is_err());
        let garbage = directory.join("kaputt.sqlite");
        std::fs::write(&garbage, b"nein").unwrap();
        let later = now + chrono::Duration::seconds(1);
        assert!(restore_backup_from_path(&state, &garbage, later).is_err());
        let still =
            with_database(&state, |database| urlaubstool_api::load_state(database)).unwrap();
        assert_eq!(still.requests.len(), 1);
        drop(state);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn req_calc_006_007_desktop_uses_stored_vocational_school_and_imported_holidays() {
        let directory = temporary_directory("student");
        let state = configured_state(&directory);
        with_database(&state, |database| {
            let mut settings = urlaubstool_api::load_state(database)?.settings.unwrap();
            settings.student_mode = true;
            settings.vocational_school_week = vec![0, 0, 2, 0, 0, 0, 0];
            urlaubstool_api::save_settings(database, settings)
        })
        .unwrap();
        let wednesday = |date: &str| {
            let student = student_calendar_for(&state, "Nordrhein-Westfalen").unwrap();
            calculate_with_student(input(date, date), student)
        };
        // Ganztägige Berufsschule am Mittwoch; ohne Ferien kommt ein Hinweis.
        let blocked = wednesday("2026-04-01").unwrap();
        assert_eq!(blocked.requested_half_units, 0);
        assert!(blocked.warnings.iter().any(|w| w.contains("Schulferien")));

        let ics = directory.join("ferien.ics");
        std::fs::write(
            &ics,
            "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20260330\r\nDTEND;VALUE=DATE:20260411\r\nSUMMARY:Osterferien - Nordrhein-Westfalen - 2026\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n",
        )
        .unwrap();
        let status = import_school_holidays_from_path(&state, &ics, chrono::Utc::now()).unwrap();
        assert_eq!(status.school_holidays.unwrap().file_name, "ferien.ics");
        let in_break = wednesday("2026-04-01").unwrap();
        assert_eq!(in_break.requested_half_units, 2);
        assert!(in_break.warnings.is_empty(), "{:?}", in_break.warnings);

        let missing = directory.join("fehlt.ics");
        assert!(import_school_holidays_from_path(&state, &missing, chrono::Utc::now()).is_err());
    }

    #[test]
    fn req_diag_004_diagnostics_report_has_no_personal_data() {
        let directory = temporary_directory("diagnostics");
        let state = configured_state(&directory);
        let info = collect_diagnostics(&state);
        assert_eq!(info.database_status, "Geöffnet");
        assert!(info.database_file.ends_with("urlaubstool.sqlite"));
        let created = chrono::DateTime::parse_from_rfc3339("2026-11-12T08:00:00Z")
            .unwrap()
            .with_timezone(&chrono::Utc);
        let report = diagnostics_report(&info, created);
        assert!(report.contains("Version: "));
        assert!(report.contains("- keine"));
        for personal in ["Änne", "Müller", "2026-03-16"] {
            assert!(!report.contains(personal), "{personal}");
        }
        let broken = AppDatabase(Mutex::new(Err("Datei defekt".into())));
        let info = collect_diagnostics(&broken);
        assert_eq!(info.database_status, "Datei defekt");
        assert!(diagnostics_report(&info, created).contains("nicht verfügbar (Datei defekt)"));
    }

    #[test]
    fn req_mig_002_desktop_migration_writes_backups_next_to_the_database() {
        let directory = temporary_directory("migration");
        let state = AppDatabase(Mutex::new(open_database(Ok(directory.clone()))));
        let source = directory.join("ledger.csv");
        let text = "SchemaVersion,Year,RequestId,CreatedAt,StartDate,EndDate,StartHalfDay,EndHalfDay,DaysRequested,Status,RejectionReason,ArchivedAt,PdfPath\n1,2026,550e8400-e29b-41d4-a716-446655440009,2026-01-02T03:04:05Z,2026-03-09,2026-03-09,false,false,1,Exported,,,\n";
        std::fs::write(&source, text).unwrap();
        let selection = preview_migration_path(&state, &source).unwrap();
        assert!(selection.preview.can_import);
        let now = chrono::DateTime::parse_from_rfc3339("2026-11-12T09:00:00Z")
            .unwrap()
            .with_timezone(&chrono::Utc);
        let result = import_migration_path(
            &state,
            &source,
            &selection.preview.source_sha256,
            false,
            now,
        )
        .unwrap();
        assert_eq!(result.requests_imported, 1);
        assert!(result
            .database_backup
            .ends_with("Sicherungen/vor-Migration_2026-11-12_09-00-00.sqlite"));
        assert_eq!(std::fs::read_to_string(&source).unwrap(), text);
        // REQ-MIG-007: Der Bericht des Imports liegt zum Speichern bereit.
        let report = LAST_MIGRATION_REPORT.lock().unwrap().clone().unwrap();
        assert!(report.contains("Anträge: 1"), "{report}");
        assert!(report.contains("Abweichungen: keine"), "{report}");
        assert!(import_migration_path(&state, &source, "falsch", false, now).is_err());
    }

    #[test]
    fn req_arch_002_desktop_preview_ignores_settings_sent_by_the_ui() {
        let directory = temporary_directory("stored-context");
        let state = configured_state(&directory);
        let mut ui = input("2026-03-21", "2026-03-21");
        // Die Oberfläche behauptet Samstag als Arbeitstag und einen anderen Anspruch.
        ui.workdays = vec![true; 7];
        ui.annual_entitlement_half_units = 999;
        let output =
            calculate_application_input(stored_context_input(&state, ui).unwrap()).unwrap();
        assert_eq!(
            output.requested_half_units, 0,
            "gespeicherte Arbeitswoche Mo–Fr gilt"
        );
        assert_eq!(output.annual_entitlement_half_units, 60);
    }
}
