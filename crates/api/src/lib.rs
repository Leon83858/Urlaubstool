#![forbid(unsafe_code)]
//! Schnittstelle zwischen Oberfläche und Anwendungsschicht (REQ-ARCH-002).
//!
//! Die Oberfläche übergibt nur Eingaben. Berechnung, Validierung und
//! Speicherung laufen hier über Anwendungs- und Speicherschicht; Uhrzeit und
//! Kennungen werden von außen übergeben, damit alle Abläufe testbar bleiben.

use std::path::Path;

use chrono::{DateTime, Datelike, NaiveDate, SecondsFormat, Utc};
use serde::{Deserialize, Serialize};
pub use urlaubstool_application::CalendarDayOutput;
use urlaubstool_application::{
    approved_half_units, calculate_vacation, calendar_year as application_calendar_year,
    ensure_approval_within_entitlement, prepare_request_pdf, ApprovalExceedsEntitlement,
    DraftRequest, PdfError, PdfPersonalDetails, PdfRequestData, RequestRepository, RequestStatus,
    SchoolBreakRecord, SchoolHolidayImport, StoredRequest, StudentCalendar, UserSettings,
    VacationCalculationInput,
};
use urlaubstool_holidays::{parse_kmk_ics, GermanState};

/// Seite des KMK-Ferienkalenders, von der die ICS-Dateien je Bundesland stammen (E2, ADR-0001).
pub const KMK_FERIENKALENDER_URL: &str =
    "https://www.kmk.org/service/ferienregelung/ferienkalender.html";

/// Herkunft der gesetzlichen Feiertage für die Anzeige des Datenstands (REQ-HOL-003).
pub const PUBLIC_HOLIDAY_SOURCE: &str = "Gesetzliche Feiertage: im Programm berechnet nach den Länderregeln der Altanwendung (Stand 10.11.2026), offline verfügbar.";

/// Größte zulässige ICS-Datei; KMK-Kalender eines Landes sind wenige Kilobyte groß.
pub const MAX_ICS_BYTES: usize = 2 * 1024 * 1024;
use urlaubstool_storage::{Database, StorageError};

/// Kürzel und Anzeigenamen der 16 Bundesländer.
pub const FEDERAL_STATES: [(&str, &str); 16] = [
    ("BW", "Baden-Württemberg"),
    ("BY", "Bayern"),
    ("BE", "Berlin"),
    ("BB", "Brandenburg"),
    ("HB", "Bremen"),
    ("HH", "Hamburg"),
    ("HE", "Hessen"),
    ("MV", "Mecklenburg-Vorpommern"),
    ("NI", "Niedersachsen"),
    ("NW", "Nordrhein-Westfalen"),
    ("RP", "Rheinland-Pfalz"),
    ("SL", "Saarland"),
    ("SN", "Sachsen"),
    ("ST", "Sachsen-Anhalt"),
    ("SH", "Schleswig-Holstein"),
    ("TH", "Thüringen"),
];

#[derive(Debug, thiserror::Error)]
pub enum ApiError {
    #[error("{0}")]
    Invalid(String),
    #[error(transparent)]
    Storage(#[from] StorageError),
    #[error(transparent)]
    Approval(#[from] ApprovalExceedsEntitlement),
    #[error(transparent)]
    Pdf(#[from] PdfError),
}

fn invalid(message: &str) -> ApiError {
    ApiError::Invalid(message.to_owned())
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsDto {
    pub first_name: String,
    pub last_name: String,
    /// Straße und PLZ/Ort für das Antragsformular; darf leer sein.
    #[serde(default)]
    pub address: String,
    pub department: String,
    #[serde(default)]
    pub personnel_number: String,
    pub class_name: String,
    pub annual_entitlement_half_units: u32,
    /// Anzeigename des Bundeslands, leer wenn noch keines gewählt ist.
    pub federal_state: String,
    /// Montag bis Sonntag.
    pub workdays: Vec<bool>,
    pub include_optional_holidays: bool,
    /// Berufsschulmodus (REQ-HOL-*: ein-/ausschaltbar).
    #[serde(default)]
    pub student_mode: bool,
    /// Montag bis Sonntag: 0 = keine, 1 = halbtags, 2 = ganztags Berufsschule.
    #[serde(default = "no_school_week")]
    pub vocational_school_week: Vec<u8>,
}

fn no_school_week() -> Vec<u8> {
    vec![0; 7]
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SchoolHolidayStatusDto {
    pub federal_state: String,
    pub file_name: String,
    pub imported_at_utc: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub latest_event_stamp_utc: Option<String>,
    pub break_count: usize,
    pub first_date: String,
    pub last_date: String,
    pub source_url: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CalendarStatusDto {
    pub public_holiday_source: String,
    /// Schulferien des eingestellten Bundeslands; `None` ohne Import.
    pub school_holidays: Option<SchoolHolidayStatusDto>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StatusEventDto {
    pub status: String,
    pub occurred_at_utc: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub previous_status: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rejection_reason: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RequestDto {
    pub id: String,
    pub start_date: String,
    pub end_date: String,
    pub start_half_day: bool,
    pub end_half_day: bool,
    pub status: String,
    pub requested_half_units: u32,
    /// Zählt gegen den Jahresanspruch (genehmigt oder nach Genehmigung archiviert).
    pub counts_as_approved: bool,
    pub calculation_version: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rejection_reason: Option<String>,
    pub aza_dates: Vec<String>,
    pub created_at_utc: String,
    pub updated_at_utc: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub export_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub exported_at_utc: Option<String>,
    pub status_events: Vec<StatusEventDto>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StateDto {
    /// `None`, solange noch keine Einstellungen gespeichert wurden.
    pub settings: Option<SettingsDto>,
    /// Alle nicht gelöschten Anträge.
    pub requests: Vec<RequestDto>,
    /// Gelöschte Anträge; sie können wiederhergestellt werden (REQ-HIST-005).
    pub deleted_requests: Vec<RequestDto>,
    pub calendar: CalendarStatusDto,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreateRequestInput {
    pub request_id: String,
    pub start_date: String,
    pub end_date: String,
    pub start_half_day: bool,
    pub end_half_day: bool,
    #[serde(default)]
    pub aza_dates: Vec<String>,
    /// Verbrauch aus der Vorschau; weicht die Neuberechnung ab, wird nichts gespeichert.
    pub expected_half_units: u32,
    /// Pfad einer bereits geschriebenen PDF-Datei, falls der Antrag exportiert wurde.
    #[serde(default)]
    pub export_path: Option<String>,
}

fn timestamp(value: DateTime<Utc>) -> String {
    value.to_rfc3339_opts(SecondsFormat::Millis, true)
}

fn state_name(code: &str) -> Option<&'static str> {
    let code = if code == "NRW" { "NW" } else { code };
    FEDERAL_STATES
        .iter()
        .find(|(known, _)| *known == code)
        .map(|(_, name)| *name)
}

fn state_code(name: &str) -> Option<&'static str> {
    FEDERAL_STATES
        .iter()
        .find(|(_, known)| *known == name)
        .map(|(code, _)| *code)
}

fn optional_text(value: &str) -> Option<String> {
    let trimmed = value.trim();
    (!trimmed.is_empty()).then(|| trimmed.to_owned())
}

fn workdays_from_mask(mask: u8) -> Vec<bool> {
    (0..7).map(|day| mask & (1 << day) != 0).collect()
}

fn settings_dto(settings: &UserSettings) -> SettingsDto {
    SettingsDto {
        first_name: settings.first_name.clone(),
        last_name: settings.last_name.clone(),
        address: settings.address.clone().unwrap_or_default(),
        department: settings.department.clone().unwrap_or_default(),
        personnel_number: settings.personnel_number.clone().unwrap_or_default(),
        class_name: settings.class_name.clone().unwrap_or_default(),
        annual_entitlement_half_units: settings.annual_entitlement_half_units,
        federal_state: settings
            .federal_state_code
            .as_deref()
            .and_then(state_name)
            .unwrap_or_default()
            .to_owned(),
        workdays: workdays_from_mask(settings.workday_mask),
        include_optional_holidays: settings.include_optional_holidays,
        student_mode: settings.student_mode,
        vocational_school_week: settings.vocational_school_week.to_vec(),
    }
}

fn parse_status_label(label: &str) -> Option<RequestStatus> {
    RequestStatus::ALL
        .into_iter()
        .find(|status| status.label() == label || status.as_str() == label)
}

fn request_dto(database: &Database, request: &StoredRequest) -> Result<RequestDto, ApiError> {
    let status_events = database
        .load_status_events(&request.request_id)?
        .into_iter()
        .map(|event| StatusEventDto {
            status: event.new_status.label().to_owned(),
            occurred_at_utc: timestamp(event.occurred_at_utc),
            previous_status: event
                .previous_status
                .map(|status| status.label().to_owned()),
            rejection_reason: event.rejection_reason,
        })
        .collect();
    let last_export = database.load_exports(&request.request_id)?.pop();
    Ok(RequestDto {
        id: request.request_id.clone(),
        start_date: request.start_date.to_string(),
        end_date: request.end_date.to_string(),
        start_half_day: request.start_half_day,
        end_half_day: request.end_half_day,
        status: request.status.label().to_owned(),
        requested_half_units: request.requested_half_units,
        counts_as_approved: request.counts_as_approved,
        calculation_version: 1,
        rejection_reason: request.rejection_reason.clone(),
        aza_dates: request.aza_dates.iter().map(ToString::to_string).collect(),
        created_at_utc: timestamp(request.created_at_utc),
        updated_at_utc: timestamp(request.updated_at_utc),
        export_path: last_export
            .as_ref()
            .map(|export| export.file_path.to_string_lossy().into_owned()),
        exported_at_utc: last_export.map(|export| timestamp(export.exported_at_utc)),
        status_events,
    })
}

fn find_request(database: &Database, request_id: &str) -> Result<RequestDto, ApiError> {
    let requests = database.load_requests(true)?;
    let request = requests
        .iter()
        .find(|request| request.request_id == request_id)
        .ok_or_else(|| StorageError::RequestNotFound(request_id.to_owned()))?;
    request_dto(database, request)
}

/// Einstellungen und alle Anträge, gelöschte getrennt (REQ-DATA-002).
pub fn load_state(database: &Database) -> Result<StateDto, ApiError> {
    let settings = database.load_settings()?.as_ref().map(settings_dto);
    let mut requests = Vec::new();
    let mut deleted_requests = Vec::new();
    for request in database.load_requests(true)? {
        let dto = request_dto(database, &request)?;
        if request.status == RequestStatus::Deleted {
            deleted_requests.push(dto);
        } else {
            requests.push(dto);
        }
    }
    Ok(StateDto {
        settings,
        requests,
        deleted_requests,
        calendar: calendar_status(database)?,
    })
}

/// Datenstand der Feiertage und Schulferien für das eingestellte Bundesland.
pub fn calendar_status(database: &Database) -> Result<CalendarStatusDto, ApiError> {
    let state_code = database
        .load_settings()?
        .and_then(|settings| settings.federal_state_code);
    let school_holidays = match state_code {
        Some(code) => database
            .load_school_holidays(&code)?
            .map(|import| school_holiday_status(&code, &import)),
        None => None,
    };
    Ok(CalendarStatusDto {
        public_holiday_source: PUBLIC_HOLIDAY_SOURCE.to_owned(),
        school_holidays,
    })
}

fn school_holiday_status(code: &str, import: &SchoolHolidayImport) -> SchoolHolidayStatusDto {
    let first = import.breaks.iter().map(|item| item.start_date).min();
    let last = import
        .breaks
        .iter()
        .filter_map(|item| item.end_date_exclusive.pred_opt())
        .max();
    SchoolHolidayStatusDto {
        federal_state: state_name(code).unwrap_or(code).to_owned(),
        file_name: import.file_name.clone(),
        imported_at_utc: timestamp(import.imported_at_utc),
        latest_event_stamp_utc: import.latest_event_stamp_utc.map(timestamp),
        break_count: import.breaks.len(),
        first_date: first.map(|date| date.to_string()).unwrap_or_default(),
        last_date: last.map(|date| date.to_string()).unwrap_or_default(),
        source_url: import.source_url.clone(),
    }
}

/// Berufsschule aus den Einstellungen und importierte Ferien des Bundeslands.
pub fn student_calendar(
    database: &Database,
    state_code: &str,
) -> Result<StudentCalendar, ApiError> {
    let settings = database.load_settings()?.unwrap_or_default();
    // Akzeptiert Kürzel („NW“, Alt-Kürzel „NRW“) und den Landesnamen der Oberfläche.
    let code = match state_code {
        "NRW" => "NW",
        other => self::state_code(other).unwrap_or(other),
    };
    let school_breaks = database.load_school_holidays(code)?.map(|import| {
        import
            .breaks
            .iter()
            .map(|item| (item.start_date, item.end_date_exclusive))
            .collect()
    });
    Ok(StudentCalendar {
        active: settings.student_mode,
        vocational_school_week: settings.vocational_school_week,
        school_breaks,
    })
}

/// Kalenderfakten eines Jahres aus den gespeicherten Einstellungen (REQ-UI-006).
pub fn calendar_year(database: &Database, year: i32) -> Result<Vec<CalendarDayOutput>, ApiError> {
    if !(1900..=2200).contains(&year) {
        return Err(invalid(
            "Das Kalenderjahr liegt außerhalb des unterstützten Bereichs.",
        ));
    }
    let settings = database
        .load_settings()?
        .ok_or_else(|| invalid("Bitte zuerst die Einstellungen speichern."))?;
    let state = settings
        .federal_state_code
        .clone()
        .ok_or_else(|| invalid("Bitte ein Bundesland auswählen."))?;
    let student = student_calendar(database, &state)?;
    application_calendar_year(
        year,
        &state,
        &workdays_from_mask(settings.workday_mask),
        settings.include_optional_holidays,
        &student,
    )
    .map_err(ApiError::Invalid)
}

/// Importiert eine ICS-Datei des KMK-Ferienkalenders für das eingestellte
/// Bundesland (REQ-HOL-003, E2). Ersetzt frühere Ferien dieses Landes vollständig;
/// bei einem Fehler bleibt alles unverändert.
/// Ein Antrag in der Jahresübersicht (REQ-UI-005).
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct YearRequestDto {
    pub id: String,
    pub start_date: String,
    pub end_date: String,
    pub status: String,
    pub requested_half_units: u32,
    pub counts_as_approved: bool,
}

/// Summen eines Urlaubsjahres, berechnet aus der Datenbank (REQ-ARCH-002, REQ-UI-005).
///
/// Ein Antrag gehört zum Jahr seines Startdatums – wie bei der Anspruchsprüfung.
/// „Beantragt“ sind offene Anträge (Entwurf, Exportiert); „Genehmigt“ alles, was
/// gegen den Anspruch zählt (auch nach Genehmigung archivierte Altfälle).
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct YearSummaryDto {
    pub year: i32,
    pub entitlement_half_units: u32,
    pub approved_half_units: u64,
    pub requested_half_units: u64,
    /// Kann negativ sein, wenn genehmigte Tage den Anspruch übersteigen.
    pub available_half_units: i64,
    /// Nicht gelöschte Anträge des Jahres, nach Beginn sortiert.
    pub requests: Vec<YearRequestDto>,
}

pub fn year_summary(database: &Database, year: i32) -> Result<YearSummaryDto, ApiError> {
    let entitlement_half_units = database
        .load_settings()?
        .map(|settings| settings.annual_entitlement_half_units)
        .unwrap_or(0);
    let mut stored: Vec<StoredRequest> = database
        .load_requests(false)?
        .into_iter()
        .filter(|request| {
            request.status != RequestStatus::Deleted && request.start_date.year() == year
        })
        .collect();
    stored.sort_by(|left, right| {
        left.start_date
            .cmp(&right.start_date)
            .then_with(|| left.request_id.cmp(&right.request_id))
    });
    let approved_half_units = approved_half_units(&stored, year, None);
    let requested_half_units = stored
        .iter()
        .filter(|request| {
            matches!(
                request.status,
                RequestStatus::Draft | RequestStatus::Exported
            )
        })
        .map(|request| u64::from(request.requested_half_units))
        .sum();
    let available_half_units =
        i64::from(entitlement_half_units) - i64::try_from(approved_half_units).unwrap_or(i64::MAX);
    Ok(YearSummaryDto {
        year,
        entitlement_half_units,
        approved_half_units,
        requested_half_units,
        available_half_units,
        requests: stored
            .iter()
            .map(|request| YearRequestDto {
                id: request.request_id.clone(),
                start_date: request.start_date.to_string(),
                end_date: request.end_date.to_string(),
                status: request.status.label().to_owned(),
                requested_half_units: request.requested_half_units,
                counts_as_approved: request.counts_as_approved,
            })
            .collect(),
    })
}

pub fn import_school_holidays(
    database: &mut Database,
    contents: &[u8],
    file_name: &str,
    now: DateTime<Utc>,
) -> Result<CalendarStatusDto, ApiError> {
    if contents.len() > MAX_ICS_BYTES {
        return Err(invalid(
            "Die Datei ist zu groß für einen Ferienkalender (höchstens 2 MB).",
        ));
    }
    let settings = database
        .load_settings()?
        .ok_or_else(|| invalid("Bitte zuerst die Einstellungen mit Bundesland speichern."))?;
    let code = settings
        .federal_state_code
        .clone()
        .ok_or_else(|| invalid("Bitte zuerst ein Bundesland auswählen."))?;
    let state = GermanState::from_code(&code)
        .ok_or_else(|| invalid("Das gespeicherte Bundesland ist unbekannt."))?;
    let text = std::str::from_utf8(contents.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(contents))
        .map_err(|_| invalid("Die Datei ist kein UTF-8-Text und damit keine KMK-ICS-Datei."))?;
    let calendar = parse_kmk_ics(state, KMK_FERIENKALENDER_URL, now, text).map_err(|errors| {
        ApiError::Invalid(format!(
            "Der Ferienkalender wurde nicht übernommen: {}",
            errors
                .iter()
                .map(|error| error.message())
                .collect::<Vec<_>>()
                .join(" ")
        ))
    })?;
    use sha2::{Digest, Sha256};
    let digest = Sha256::digest(contents);
    let content_sha256 = digest.iter().map(|byte| format!("{byte:02x}")).collect();
    let file_name = std::path::Path::new(file_name)
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("Ferienkalender.ics")
        .to_owned();
    database.replace_school_holidays(&SchoolHolidayImport {
        state_code: code,
        source_url: calendar.source_url.clone(),
        file_name,
        imported_at_utc: now,
        latest_event_stamp_utc: calendar.latest_event_stamp_utc,
        content_sha256,
        breaks: calendar
            .breaks()
            .iter()
            .map(|item| SchoolBreakRecord {
                start_date: item.start_date,
                end_date_exclusive: item.end_date_exclusive,
                summary: item.summary.clone(),
            })
            .collect(),
    })?;
    calendar_status(database)
}

/// Speichert die in der Oberfläche bearbeitbaren Einstellungen. Felder, die die
/// Oberfläche noch nicht kennt (z. B. Berufsschule, Exportpfad), bleiben erhalten.
pub fn save_settings(database: &Database, input: SettingsDto) -> Result<SettingsDto, ApiError> {
    if input.annual_entitlement_half_units == 0 {
        return Err(invalid(
            "Der Jahresurlaub muss größer als 0 und in halben Tagen angegeben sein.",
        ));
    }
    if input.workdays.len() != 7 {
        return Err(invalid(
            "Die Arbeitswoche muss sieben Wochentage enthalten.",
        ));
    }
    if !input.workdays.iter().any(|day| *day) {
        return Err(invalid("Bitte mindestens einen Arbeitstag auswählen."));
    }
    let vocational_school_week: [u8; 7] = input
        .vocational_school_week
        .as_slice()
        .try_into()
        .ok()
        .filter(|week: &[u8; 7]| week.iter().all(|value| *value <= 2))
        .ok_or_else(|| {
            invalid(
                "Die Berufsschulwoche muss sieben Angaben (keine, halbtags, ganztags) enthalten.",
            )
        })?;
    let code = state_code(&input.federal_state)
        .ok_or_else(|| invalid("Bitte ein Bundesland auswählen."))?;
    let mut settings = database.load_settings()?.unwrap_or_default();
    settings.first_name = input.first_name.trim().to_owned();
    settings.last_name = input.last_name.trim().to_owned();
    settings.address = optional_text(&input.address);
    settings.department = optional_text(&input.department);
    settings.personnel_number = optional_text(&input.personnel_number);
    settings.class_name = optional_text(&input.class_name);
    settings.annual_entitlement_half_units = input.annual_entitlement_half_units;
    settings.federal_state_code = Some(code.to_owned());
    settings.workday_mask = input
        .workdays
        .iter()
        .enumerate()
        .fold(0, |mask, (day, active)| mask | (u8::from(*active) << day));
    settings.include_optional_holidays = input.include_optional_holidays;
    settings.student_mode = input.student_mode;
    settings.vocational_school_week = vocational_school_week;
    database.save_settings(&settings)?;
    Ok(settings_dto(&settings))
}

/// Berechnet den Antrag mit den gespeicherten Einstellungen neu und legt ihn an.
pub fn create_request(
    database: &mut Database,
    input: CreateRequestInput,
    now: DateTime<Utc>,
) -> Result<RequestDto, ApiError> {
    if input.request_id.trim().is_empty() {
        return Err(invalid("Dem Antrag fehlt eine Kennung."));
    }
    let export_path = match input.export_path.as_deref().map(str::trim) {
        Some("") => return Err(invalid("Der Exportpfad ist leer.")),
        other => other.map(str::to_owned),
    };
    let settings = database
        .load_settings()?
        .ok_or_else(|| invalid("Bitte zuerst die Einstellungen speichern."))?;
    let federal_state = settings
        .federal_state_code
        .clone()
        .ok_or_else(|| invalid("Bitte ein Bundesland auswählen."))?;
    let parse = |value: &str, message: &str| {
        NaiveDate::parse_from_str(value, "%Y-%m-%d").map_err(|_| invalid(message))
    };
    let start_date = parse(&input.start_date, "Ungültiges Startdatum.")?;
    let end_date = parse(&input.end_date, "Ungültiges Enddatum.")?;
    let aza_dates = input
        .aza_dates
        .iter()
        .map(|date| parse(date, "Ungültiges AZA-Datum."))
        .collect::<Result<Vec<_>, _>>()?;
    let stored = database.load_requests(false)?;
    let already_approved = u32::try_from(approved_half_units(&stored, start_date.year(), None))
        .map_err(|_| invalid("Die Summe der genehmigten Tage ist ungültig."))?;
    let calculation = calculate_vacation(VacationCalculationInput {
        start_date: input.start_date.clone(),
        end_date: input.end_date.clone(),
        start_half_day: input.start_half_day,
        end_half_day: input.end_half_day,
        workdays: workdays_from_mask(settings.workday_mask),
        federal_state,
        annual_entitlement_half_units: settings.annual_entitlement_half_units,
        already_approved_half_units: already_approved,
        aza_dates: input.aza_dates.clone(),
        include_optional_holidays: settings.include_optional_holidays,
        student: student_calendar(
            database,
            settings.federal_state_code.as_deref().unwrap_or(""),
        )?,
    })
    .map_err(ApiError::Invalid)?;
    if let Some(error) = calculation.errors.first() {
        return Err(ApiError::Invalid(error.clone()));
    }
    if calculation.requested_half_units == 0 {
        return Err(invalid(
            "Der Zeitraum enthält keine anrechenbaren Urlaubstage.",
        ));
    }
    if calculation.requested_half_units != input.expected_half_units {
        return Err(invalid(
            "Die Berechnung mit den gespeicherten Einstellungen weicht von der Vorschau ab. Bitte Einstellungen speichern und den Antrag erneut prüfen.",
        ));
    }
    database.create_draft(&DraftRequest {
        request_id: input.request_id.clone(),
        start_date,
        end_date,
        start_half_day: input.start_half_day,
        end_half_day: input.end_half_day,
        requested_half_units: calculation.requested_half_units,
        created_at_utc: now,
        aza_dates,
    })?;
    if let Some(path) = export_path {
        database.record_export(&input.request_id, Path::new(&path), None, now)?;
    }
    find_request(database, &input.request_id)
}

/// Statuswechsel aus der Historie. „Exportiert“ entsteht nur durch einen Export.
pub fn change_request_status(
    database: &mut Database,
    request_id: &str,
    status_label: &str,
    rejection_reason: Option<&str>,
    now: DateTime<Utc>,
) -> Result<RequestDto, ApiError> {
    let status = parse_status_label(status_label)
        .ok_or_else(|| ApiError::Invalid(format!("Unbekannter Status „{status_label}“.")))?;
    let requests = database.load_requests(true)?;
    let candidate = requests
        .iter()
        .find(|request| request.request_id == request_id)
        .ok_or_else(|| StorageError::RequestNotFound(request_id.to_owned()))?;
    if status == RequestStatus::Exported && candidate.status != RequestStatus::Deleted {
        return Err(invalid(
            "Der Status „Exportiert“ entsteht nur durch einen PDF-Export.",
        ));
    }
    if status == RequestStatus::Deleted && candidate.counts_as_approved {
        return Err(invalid(
            "Dieser Antrag zählt als genehmigt und kann nicht gelöscht werden. Bitte zuerst ablehnen.",
        ));
    }
    if status == RequestStatus::Approved {
        let settings = database
            .load_settings()?
            .ok_or_else(|| invalid("Bitte zuerst die Einstellungen speichern."))?;
        ensure_approval_within_entitlement(
            &requests,
            candidate,
            settings.annual_entitlement_half_units,
        )?;
    }
    database.change_status(request_id, status, now, rejection_reason)?;
    find_request(database, request_id)
}

/// Ersetzt in einer Berechnungsanfrage der Oberfläche alle fachlichen Grundlagen
/// durch den gespeicherten Stand (REQ-ARCH-002): Einstellungen, Berufsschule,
/// Ferien und die Summe der bereits genehmigten Tage des Antragsjahres. Von der
/// Oberfläche werden nur Zeitraum, Halbtage und AZA-Tage übernommen.
pub fn apply_stored_context(
    database: &Database,
    input: &mut VacationCalculationInput,
) -> Result<(), ApiError> {
    let settings = database
        .load_settings()?
        .ok_or_else(|| invalid("Bitte zuerst die Einstellungen speichern."))?;
    let state_code = settings
        .federal_state_code
        .clone()
        .ok_or_else(|| invalid("Bitte ein Bundesland auswählen."))?;
    let year = NaiveDate::parse_from_str(&input.start_date, "%Y-%m-%d")
        .map_err(|_| invalid("Ungültiges Startdatum."))?
        .year();
    let stored = database.load_requests(false)?;
    input.already_approved_half_units = u32::try_from(approved_half_units(&stored, year, None))
        .map_err(|_| invalid("Die Summe der genehmigten Tage ist ungültig."))?;
    input.workdays = workdays_from_mask(settings.workday_mask);
    input.annual_entitlement_half_units = settings.annual_entitlement_half_units;
    input.include_optional_holidays = settings.include_optional_holidays;
    input.student = student_calendar(database, &state_code)?;
    input.federal_state = state_code;
    Ok(())
}

fn calculation_input(
    database: &Database,
    settings: &UserSettings,
    request: &StoredRequest,
    already_approved_half_units: u32,
) -> Result<VacationCalculationInput, ApiError> {
    let state_code = settings
        .federal_state_code
        .clone()
        .ok_or_else(|| invalid("Bitte ein Bundesland auswählen."))?;
    Ok(VacationCalculationInput {
        student: student_calendar(database, &state_code)?,
        start_date: request.start_date.to_string(),
        end_date: request.end_date.to_string(),
        start_half_day: request.start_half_day,
        end_half_day: request.end_half_day,
        workdays: workdays_from_mask(settings.workday_mask),
        federal_state: settings
            .federal_state_code
            .clone()
            .ok_or_else(|| invalid("Bitte ein Bundesland auswählen."))?,
        annual_entitlement_half_units: settings.annual_entitlement_half_units,
        already_approved_half_units,
        aza_dates: request.aza_dates.iter().map(ToString::to_string).collect(),
        include_optional_holidays: settings.include_optional_holidays,
    })
}

/// Bereitet das PDF eines gespeicherten Antrags vor (REQ-HIST-006, REQ-PDF-005).
///
/// Der Antrag wird mit den gespeicherten Einstellungen neu berechnet. Weicht der
/// Verbrauch vom gespeicherten Wert ab, wird kein PDF erstellt, damit das
/// Formular nie andere Werte zeigt als die Historie.
pub fn prepare_stored_request_pdf(
    database: &Database,
    request_id: &str,
    application_date: NaiveDate,
) -> Result<PdfRequestData, ApiError> {
    let settings = database
        .load_settings()?
        .ok_or_else(|| invalid("Bitte zuerst die Einstellungen speichern."))?;
    let requests = database.load_requests(true)?;
    let request = requests
        .iter()
        .find(|request| request.request_id == request_id)
        .ok_or_else(|| StorageError::RequestNotFound(request_id.to_owned()))?;
    if request.status == RequestStatus::Deleted {
        return Err(invalid(
            "Ein gelöschter Antrag kann nicht exportiert werden. Bitte zuerst wiederherstellen.",
        ));
    }
    let already_approved = u32::try_from(approved_half_units(
        &requests,
        request.start_date.year(),
        Some(request_id),
    ))
    .map_err(|_| invalid("Die Summe der genehmigten Tage ist ungültig."))?;
    let optional = |value: &Option<String>| {
        value
            .as_deref()
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .map(str::to_owned)
    };
    let data = prepare_request_pdf(
        calculation_input(database, &settings, request, already_approved)?,
        PdfPersonalDetails {
            first_name: settings.first_name.clone(),
            last_name: settings.last_name.clone(),
            address: optional(&settings.address),
            department: optional(&settings.department),
            personnel_number: optional(&settings.personnel_number),
            class_name: optional(&settings.class_name),
        },
        application_date,
    )?;
    if data.requested_half_units != request.requested_half_units {
        return Err(invalid(
            "Mit den aktuellen Einstellungen ergibt sich ein anderer Urlaubsverbrauch als gespeichert. Das PDF wurde nicht erstellt; bitte den Antrag neu anlegen.",
        ));
    }
    Ok(data)
}

/// Hält eine geschriebene PDF-Datei für einen gespeicherten Antrag fest.
pub fn record_request_export(
    database: &mut Database,
    request_id: &str,
    file_path: &Path,
    now: DateTime<Utc>,
) -> Result<RequestDto, ApiError> {
    database.record_export(request_id, file_path, None, now)?;
    find_request(database, request_id)
}

/// Schreibt eine vollständige Sicherung in eine neue Datei (REQ-DATA-004).
pub fn backup_database(database: &Database, destination: &Path) -> Result<(), ApiError> {
    database.backup_to_new(destination)?;
    Ok(())
}

/// Ersetzt alle Daten durch eine Sicherung; der vorherige Stand wird vorher nach
/// `safety_backup` gesichert (REQ-DATA-004).
pub fn restore_database(
    database: &mut Database,
    backup: &Path,
    safety_backup: &Path,
) -> Result<StateDto, ApiError> {
    database.restore_from_backup(backup, safety_backup)?;
    load_state(database)
}

/// Ein Problem in einer Altdatei mit Datensatzbezug (REQ-MIG-003).
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MigrationIssueDto {
    pub record_number: usize,
    pub kind: String,
    pub message: String,
}

/// Vorschau einer Altdatei vor dem Import (REQ-MIG-001, REQ-MIG-003).
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MigrationPreviewDto {
    pub file_name: String,
    pub format: String,
    pub source_sha256: String,
    pub records_read: usize,
    pub records_ready: usize,
    pub records_skipped: usize,
    pub records_failed: usize,
    pub issues: Vec<MigrationIssueDto>,
    pub issues_truncated: bool,
    pub can_import: bool,
    pub needs_confirmation: bool,
    /// Der Import ersetzt die aktuell gespeicherten Einstellungen.
    pub replaces_settings: bool,
}

/// Mengen und Summen eines Jahres: Altdatei gegen Datenbank (REQ-MIG-004).
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ReconciliationYearDto {
    pub year: i32,
    pub source_requests: usize,
    pub imported_requests: usize,
    pub source_half_units: u64,
    pub imported_half_units: u64,
    pub source_approved_half_units: u64,
    pub imported_approved_half_units: u64,
}

/// Abgleich nach dem Import. `differences` leer = ohne Verlust oder Abweichung übernommen.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ReconciliationDto {
    pub years: Vec<ReconciliationYearDto>,
    pub differences: Vec<String>,
}

/// Vergleicht die Anträge einer Altdatei mit den gespeicherten Anträgen gleicher Kennung:
/// Vollständigkeit, Zeitraum, Halbtage, Tage, Status, AZA und Summen je Jahr (REQ-MIG-004).
pub fn reconcile_import(
    batch: &urlaubstool_application::LegacyImportBatch,
    stored: &[StoredRequest],
) -> ReconciliationDto {
    use std::collections::BTreeMap;
    use urlaubstool_application::RequestStatus as Status;
    let by_id: BTreeMap<&str, &StoredRequest> =
        stored.iter().map(|r| (r.request_id.as_str(), r)).collect();
    let approved_in_source = |request: &urlaubstool_application::MigratedRequest| {
        request.status == Status::Approved
            || (request.status == Status::Archived
                && batch
                    .status_events
                    .iter()
                    .rfind(|e| {
                        e.request_id == request.request_id && e.new_status == Status::Archived
                    })
                    .is_some_and(|e| e.previous_status == Some(Status::Approved)))
    };
    let mut years: BTreeMap<i32, ReconciliationYearDto> = BTreeMap::new();
    let mut differences = Vec::new();
    for source in &batch.requests {
        let year = source.start_date.year();
        let entry = years.entry(year).or_insert(ReconciliationYearDto {
            year,
            source_requests: 0,
            imported_requests: 0,
            source_half_units: 0,
            imported_half_units: 0,
            source_approved_half_units: 0,
            imported_approved_half_units: 0,
        });
        entry.source_requests += 1;
        entry.source_half_units += u64::from(source.requested_half_units);
        if approved_in_source(source) {
            entry.source_approved_half_units += u64::from(source.requested_half_units);
        }
        let Some(target) = by_id.get(source.request_id.as_str()) else {
            differences.push(format!(
                "Antrag {} fehlt nach dem Import.",
                source.request_id
            ));
            continue;
        };
        entry.imported_requests += 1;
        entry.imported_half_units += u64::from(target.requested_half_units);
        if target.counts_as_approved {
            entry.imported_approved_half_units += u64::from(target.requested_half_units);
        }
        let mut fields = Vec::new();
        if (source.start_date, source.end_date) != (target.start_date, target.end_date) {
            fields.push("Zeitraum");
        }
        if (source.start_half_day, source.end_half_day)
            != (target.start_half_day, target.end_half_day)
        {
            fields.push("Halbtage");
        }
        if source.requested_half_units != target.requested_half_units {
            fields.push("Urlaubstage");
        }
        if source.status != target.status {
            fields.push("Status");
        }
        let mut source_aza = source.aza_dates.clone();
        source_aza.sort();
        let mut target_aza = target.aza_dates.clone();
        target_aza.sort();
        if source_aza != target_aza {
            fields.push("AZA-Tage");
        }
        if !fields.is_empty() {
            differences.push(format!(
                "Antrag {} weicht ab: {}.",
                source.request_id,
                fields.join(", ")
            ));
        }
    }
    for year in years.values() {
        if year.source_half_units != year.imported_half_units
            || year.source_approved_half_units != year.imported_approved_half_units
        {
            differences.push(format!(
                "Summen {} weichen ab: Altdatei {} / genehmigt {}, Datenbank {} / genehmigt {} halbe Tage.",
                year.year,
                year.source_half_units,
                year.source_approved_half_units,
                year.imported_half_units,
                year.imported_approved_half_units
            ));
        }
    }
    ReconciliationDto {
        years: years.into_values().collect(),
        differences,
    }
}

/// Bei ungeklärter Abweichung wird der Stand vor dem Import zurückgeholt und der
/// Import als Fehler gemeldet (REQ-MIG-004, REQ-MIG-005).
fn reject_unreconciled_import(
    database: &mut Database,
    reconciliation: &ReconciliationDto,
    database_backup: &Path,
    safety_backup: &Path,
) -> Result<(), ApiError> {
    if reconciliation.differences.is_empty() {
        return Ok(());
    }
    database.restore_from_backup(database_backup, safety_backup)?;
    Err(ApiError::Invalid(format!(
        "Der Abgleich nach dem Import hat Abweichungen gefunden; die Datenbank wurde auf den Stand vor dem Import zurückgesetzt. {}",
        reconciliation.differences.join(" ")
    )))
}

/// Ergebnis eines Imports samt Sicherungen und neuem Datenstand (REQ-MIG-002, REQ-MIG-007).
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MigrationResultDto {
    pub file_name: String,
    pub source_backup: String,
    pub database_backup: String,
    pub already_imported: bool,
    pub settings_imported: bool,
    pub requests_imported: u64,
    pub status_events_imported: u64,
    pub exports_imported: u64,
    pub reconciliation: ReconciliationDto,
    pub state: StateDto,
}

const MAX_SHOWN_ISSUES: usize = 200;

fn format_label(format: &urlaubstool_migration::LegacyFormat) -> String {
    use urlaubstool_migration::LegacyFormat;
    match format {
        LegacyFormat::SettingsJson { schema_version } => {
            format!("Einstellungen (settings.json, Version {schema_version})")
        }
        LegacyFormat::LedgerCsv { schema_version } => {
            format!("Antragsliste (ledger.csv, Version {schema_version})")
        }
        LegacyFormat::HistoryCsv => "Verlauf (history.csv)".into(),
        LegacyFormat::HistoryJsonl => "Verlauf (history.jsonl)".into(),
    }
}

fn issue_label(kind: urlaubstool_migration::IssueKind) -> &'static str {
    use urlaubstool_migration::IssueKind;
    match kind {
        IssueKind::InvalidEncoding => "Zeichenkodierung",
        IssueKind::InvalidHeader => "Kopfzeile",
        IssueKind::InvalidRecord => "Datensatz ungültig",
        IssueKind::UnsupportedVersion => "Version nicht unterstützt",
        IssueKind::UnsupportedStatus => "Status nicht unterstützt",
        IssueKind::UnknownEventType => "Unbekanntes Ereignis",
        IssueKind::InvalidJson => "JSON ungültig",
    }
}

fn file_name_of(path: &Path) -> String {
    path.file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_default()
}

fn migration_preview(path: &Path) -> Result<urlaubstool_migration::MigrationPreview, ApiError> {
    urlaubstool_migration::preview_file(path).map_err(|error| ApiError::Invalid(error.to_string()))
}

/// Liest eine Altdatei (settings.json, ledger.csv, history.csv, history.jsonl) nur lesend
/// und zeigt, was importiert würde. Die Datei wird nicht verändert.
/// Textbericht eines Altdaten-Imports zum Speichern (REQ-MIG-007).
///
/// Enthält Zähler, Abgleich je Jahr, Abweichungen und Sicherungspfade, aber keine
/// Inhalte der Anträge oder Einstellungen.
pub fn migration_report(result: &MigrationResultDto, created_at: DateTime<Utc>) -> String {
    let format_days = |half_units: u64| {
        format!(
            "{},{}",
            half_units / 2,
            if half_units % 2 == 1 { 5 } else { 0 }
        )
    };
    let mut lines = vec![
        "Urlaubstool – Bericht zur Übernahme von Altdaten".to_owned(),
        format!("Erstellt (UTC): {}", timestamp(created_at)),
        format!("Version: {}", env!("CARGO_PKG_VERSION")),
        String::new(),
        format!("Datei: {}", result.file_name),
        format!(
            "Ergebnis: {}",
            if result.already_imported {
                "bereits früher importiert, nichts doppelt übernommen"
            } else {
                "importiert"
            }
        ),
        format!(
            "Einstellungen übernommen: {}",
            if result.settings_imported {
                "ja"
            } else {
                "nein"
            }
        ),
        format!("Anträge: {}", result.requests_imported),
        format!("Statusereignisse: {}", result.status_events_imported),
        format!("PDF-Nachweise: {}", result.exports_imported),
        String::new(),
        "Abgleich je Jahr (Altdatei → Datenbank):".to_owned(),
    ];
    if result.reconciliation.years.is_empty() {
        lines.push("  keine Anträge in der Datei".to_owned());
    }
    for year in &result.reconciliation.years {
        lines.push(format!(
            "  {}: Anträge {} → {}, Tage {} → {}, davon genehmigt {} → {}",
            year.year,
            year.source_requests,
            year.imported_requests,
            format_days(year.source_half_units),
            format_days(year.imported_half_units),
            format_days(year.source_approved_half_units),
            format_days(year.imported_approved_half_units),
        ));
    }
    lines.push(String::new());
    if result.reconciliation.differences.is_empty() {
        lines.push("Abweichungen: keine".to_owned());
    } else {
        lines.push("Abweichungen:".to_owned());
        lines.extend(
            result
                .reconciliation
                .differences
                .iter()
                .map(|text| format!("  - {text}")),
        );
    }
    lines.push(String::new());
    lines.push(format!("Sicherung der Altdatei: {}", result.source_backup));
    lines.push(format!(
        "Sicherung der Datenbank vor dem Import: {}",
        result.database_backup
    ));
    lines.push(String::new());
    lines.join("\n")
}

pub fn preview_legacy_file(
    database: &Database,
    path: &Path,
) -> Result<MigrationPreviewDto, ApiError> {
    let preview = migration_preview(path)?;
    let replaces_settings = matches!(
        preview.format,
        urlaubstool_migration::LegacyFormat::SettingsJson { .. }
    ) && database.load_settings()?.is_some();
    Ok(MigrationPreviewDto {
        file_name: file_name_of(path),
        format: format_label(&preview.format),
        source_sha256: preview.source_sha256.clone(),
        records_read: preview.records_read,
        records_ready: preview.records_ready,
        records_skipped: preview.records_skipped,
        records_failed: preview.records_failed,
        issues_truncated: preview.issues.len() > MAX_SHOWN_ISSUES,
        issues: preview
            .issues
            .iter()
            .take(MAX_SHOWN_ISSUES)
            .map(|issue| MigrationIssueDto {
                record_number: issue.record_number,
                kind: issue_label(issue.kind).into(),
                message: issue.message.clone(),
            })
            .collect(),
        can_import: preview.can_import(),
        needs_confirmation: preview.records_skipped > 0,
        replaces_settings,
    })
}

/// Importiert eine Altdatei nach erneuter Prüfung (REQ-MIG-002…005): Die Datei muss
/// unverändert zur gezeigten Vorschau sein; vorher werden die Quelldatei und die
/// Datenbank in `backup_directory` gesichert. Die Originaldatei wird nie verändert.
pub fn import_legacy_file(
    database: &mut Database,
    path: &Path,
    expected_sha256: &str,
    confirm_skipped_records: bool,
    backup_directory: &Path,
    now: DateTime<Utc>,
) -> Result<MigrationResultDto, ApiError> {
    let preview = migration_preview(path)?;
    if preview.source_sha256 != expected_sha256 {
        return Err(invalid(
            "Die Datei wurde seit der Vorschau verändert. Bitte die Vorschau erneut öffnen; es wurde nichts importiert.",
        ));
    }
    if !preview.can_import() {
        return Err(invalid(
            "Die Datei enthält fehlerhafte oder keine importierbaren Datensätze; es wurde nichts importiert.",
        ));
    }
    if preview.records_skipped > 0 && !confirm_skipped_records {
        return Err(invalid(
            "Bitte bestätigen, dass die übersprungenen Datensätze nicht übernommen werden.",
        ));
    }
    let stamp = now.format("%Y-%m-%d_%H-%M-%S");
    let file_name = file_name_of(path);
    let database_backup = backup_directory.join(format!("vor-Migration_{stamp}.sqlite"));
    let source_backup = backup_directory.join(format!("Altdaten_{stamp}_{file_name}"));
    std::fs::create_dir_all(backup_directory).map_err(|error| {
        ApiError::Invalid(format!(
            "Der Sicherungsordner konnte nicht angelegt werden; es wurde nichts importiert: {error}"
        ))
    })?;
    database.backup_to_new(&database_backup)?;
    let receipt = urlaubstool_migration::commit_preview(
        database,
        &preview,
        &source_backup,
        now,
        now,
        confirm_skipped_records,
    )
    .map_err(|error| ApiError::Invalid(format!("{error} Es wurde nichts importiert.")))?;
    let expected =
        urlaubstool_migration::expected_batch(&preview, &source_backup, confirm_skipped_records)
            .map_err(|error| {
                ApiError::Invalid(format!(
                    "Der Datenabgleich konnte nicht durchgeführt werden: {error}"
                ))
            })?;
    let reconciliation = reconcile_import(&expected, &database.load_requests(true)?);
    if !receipt.already_imported {
        let safety = backup_directory.join(format!("nach-abgebrochener-Migration_{stamp}.sqlite"));
        reject_unreconciled_import(database, &reconciliation, &database_backup, &safety)?;
    }
    Ok(MigrationResultDto {
        file_name,
        source_backup: source_backup.to_string_lossy().into_owned(),
        database_backup: database_backup.to_string_lossy().into_owned(),
        already_imported: receipt.already_imported,
        settings_imported: receipt.settings_imported,
        requests_imported: receipt.requests_imported,
        status_events_imported: receipt.status_events_imported,
        exports_imported: receipt.exports_imported,
        reconciliation,
        state: load_state(database)?,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn req_mig_005_unreconciled_import_restores_the_previous_database() {
        let directory =
            std::env::temp_dir().join(format!("urlaubstool-api-reconcile-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&directory);
        std::fs::create_dir_all(&directory).unwrap();
        let mut database = Database::open(directory.join("urlaubstool.sqlite")).unwrap();
        let before = directory.join("vor.sqlite");
        database.backup_to_new(&before).unwrap();
        // Simulierter Import: Einstellungen nach der Sicherung geändert.
        database
            .save_settings(&UserSettings {
                first_name: "Nach".into(),
                annual_entitlement_half_units: 50,
                ..UserSettings::default()
            })
            .unwrap();
        let clean = ReconciliationDto {
            years: vec![],
            differences: vec![],
        };
        reject_unreconciled_import(&mut database, &clean, &before, &directory.join("a.sqlite"))
            .unwrap();
        assert!(
            database.load_settings().unwrap().is_some(),
            "ohne Abweichung bleibt alles"
        );
        let broken = ReconciliationDto {
            years: vec![],
            differences: vec!["Antrag x fehlt nach dem Import.".into()],
        };
        let error = reject_unreconciled_import(
            &mut database,
            &broken,
            &before,
            &directory.join("b.sqlite"),
        )
        .unwrap_err()
        .to_string();
        assert!(error.contains("zurückgesetzt") && error.contains("Antrag x fehlt"));
        assert!(
            database.load_settings().unwrap().is_none(),
            "Stand vor dem Import"
        );
        assert!(
            directory.join("b.sqlite").exists(),
            "Zustand mit Abweichung bleibt gesichert"
        );
        let _ = std::fs::remove_dir_all(&directory);
    }
}
