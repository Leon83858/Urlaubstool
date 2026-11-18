use std::collections::{BTreeMap, BTreeSet};
use std::fs::{self, File, OpenOptions};
use std::io;
use std::path::{Path, PathBuf};

use chrono::{DateTime, Datelike, NaiveDate, Utc};
use rusqlite::backup::Progress;
use rusqlite::{
    params, Connection, OpenFlags, OptionalExtension, Transaction, TransactionBehavior,
};
use thiserror::Error;
use urlaubstool_application::{
    validate_status_transition, DraftRequest, ExportRecord, ImportCommitReport, LegacyImportBatch,
    MigrationRepository, PdfError, RequestRepository, RequestStatus, SchoolBreakRecord,
    SchoolHolidayImport, SettingsRepository, StatusEvent, StatusTransitionError, StoredRequest,
    UserSettings,
};

pub const CURRENT_SCHEMA_VERSION: i32 = 2;

const MIGRATION_1: &str = "
CREATE TABLE settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    schema_version INTEGER NOT NULL,
    first_name TEXT NOT NULL,
    last_name TEXT NOT NULL,
    address TEXT,
    department TEXT,
    personnel_number TEXT,
    class_name TEXT,
    annual_entitlement_half_units INTEGER NOT NULL CHECK (annual_entitlement_half_units >= 0),
    workday_mask INTEGER NOT NULL CHECK (workday_mask BETWEEN 0 AND 127),
    federal_state_code TEXT,
    student_mode INTEGER NOT NULL CHECK (student_mode IN (0, 1)),
    vocational_school_week BLOB NOT NULL CHECK (length(vocational_school_week) = 7),
    include_optional_holidays INTEGER NOT NULL CHECK (include_optional_holidays IN (0, 1)),
    export_path TEXT,
    calendar_colors_json TEXT NOT NULL
);

CREATE TABLE vacation_requests (
    request_id TEXT PRIMARY KEY,
    schema_version INTEGER NOT NULL,
    year INTEGER NOT NULL,
    start_date TEXT NOT NULL,
    end_date TEXT NOT NULL CHECK (end_date >= start_date),
    start_half_day INTEGER NOT NULL CHECK (start_half_day IN (0, 1)),
    end_half_day INTEGER NOT NULL CHECK (end_half_day IN (0, 1)),
    requested_half_units INTEGER NOT NULL CHECK (requested_half_units >= 0),
    status TEXT NOT NULL CHECK (status IN ('Draft', 'Exported', 'Approved', 'Rejected', 'Archived', 'Deleted')),
    rejection_reason TEXT,
    archived_at_utc TEXT,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE request_aza_dates (
    request_id TEXT NOT NULL REFERENCES vacation_requests(request_id) ON DELETE CASCADE,
    date TEXT NOT NULL,
    PRIMARY KEY (request_id, date)
);

CREATE TABLE request_status_events (
    event_id TEXT PRIMARY KEY,
    request_id TEXT NOT NULL REFERENCES vacation_requests(request_id) ON DELETE RESTRICT,
    previous_status TEXT,
    new_status TEXT NOT NULL CHECK (new_status IN ('Draft', 'Exported', 'Approved', 'Rejected', 'Archived', 'Deleted')),
    occurred_at_utc TEXT NOT NULL,
    rejection_reason TEXT
);

CREATE TABLE export_records (
    export_id TEXT PRIMARY KEY,
    request_id TEXT NOT NULL REFERENCES vacation_requests(request_id) ON DELETE RESTRICT,
    file_path TEXT NOT NULL,
    content_sha256 TEXT,
    exported_at_utc TEXT NOT NULL
);
";

const MIGRATION_2: &str = "
CREATE TABLE calendar_sources (
    source_type TEXT NOT NULL CHECK (source_type IN ('public_holidays', 'school_vacations')),
    state_code TEXT NOT NULL,
    source_url TEXT NOT NULL,
    fetched_at_utc TEXT NOT NULL,
    latest_event_stamp_utc TEXT,
    content_sha256 TEXT NOT NULL,
    data_version TEXT NOT NULL,
    PRIMARY KEY (source_type, state_code)
);

CREATE TABLE public_holidays (
    state_code TEXT NOT NULL,
    date TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('mandatory', 'optional')),
    name TEXT NOT NULL,
    source_url TEXT NOT NULL,
    data_version TEXT NOT NULL,
    PRIMARY KEY (state_code, date, kind)
);

CREATE TABLE school_breaks (
    state_code TEXT NOT NULL,
    start_date TEXT NOT NULL,
    end_date_exclusive TEXT NOT NULL CHECK (end_date_exclusive > start_date),
    summary TEXT NOT NULL,
    source_url TEXT NOT NULL,
    data_version TEXT NOT NULL,
    PRIMARY KEY (state_code, start_date, end_date_exclusive, source_url)
);

CREATE TABLE migration_runs (
    migration_id TEXT PRIMARY KEY,
    source_path TEXT NOT NULL,
    source_sha256 TEXT NOT NULL,
    started_at_utc TEXT NOT NULL,
    completed_at_utc TEXT,
    status TEXT NOT NULL CHECK (status IN ('preview', 'committed', 'failed', 'cancelled')),
    records_read INTEGER NOT NULL DEFAULT 0 CHECK (records_read >= 0),
    records_imported INTEGER NOT NULL DEFAULT 0 CHECK (records_imported >= 0),
    records_skipped INTEGER NOT NULL DEFAULT 0 CHECK (records_skipped >= 0),
    records_failed INTEGER NOT NULL DEFAULT 0 CHECK (records_failed >= 0),
    report_json TEXT NOT NULL DEFAULT '{}'
);

CREATE INDEX idx_vacation_requests_year_status ON vacation_requests(year, status);
CREATE INDEX idx_status_events_request_time ON request_status_events(request_id, occurred_at_utc);
";

#[derive(Debug, Error)]
pub enum StorageError {
    #[error("Datenbankfehler: {0}")]
    Sqlite(#[from] rusqlite::Error),
    #[error("Dateifehler bei {path}: {source}")]
    Io {
        path: PathBuf,
        #[source]
        source: io::Error,
    },
    #[error("Das Datenbankschema {found} ist neuer als unterstützt ({supported}).")]
    UnsupportedSchema { found: i32, supported: i32 },
    #[error("Datenbankprüfung fehlgeschlagen: {0}")]
    Integrity(String),
    #[error("Ungültige Einstellungen in der Datenbank: {0}")]
    InvalidSettings(String),
    #[error("Der Zielpfad existiert bereits: {0}")]
    DestinationExists(PathBuf),
    #[error("Die Migration wurde wegen eines bestehenden oder widersprüchlichen Datensatzes abgebrochen: {0}")]
    MigrationConflict(String),
    #[error("Der Migrationsbatch ist ungültig: {0}")]
    InvalidMigrationBatch(String),
    #[error("Der Antrag ist ungültig: {0}")]
    InvalidRequest(String),
    #[error("Die Sicherung kann nicht verwendet werden: {0}")]
    InvalidBackup(String),
    #[error("Die Kalenderdaten sind ungültig: {0}")]
    InvalidCalendar(String),
    #[error("Der Antrag {0:?} wurde nicht gefunden.")]
    RequestNotFound(String),
    #[error(transparent)]
    StatusTransition(#[from] StatusTransitionError),
    #[error(transparent)]
    Pdf(#[from] PdfError),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SettingsRow {
    pub schema_version: i32,
    pub first_name: String,
    pub last_name: String,
    pub address: Option<String>,
    pub department: Option<String>,
    pub personnel_number: Option<String>,
    pub class_name: Option<String>,
    pub annual_entitlement_half_units: u32,
    pub workday_mask: u8,
    pub federal_state_code: Option<String>,
    pub student_mode: bool,
    pub vocational_school_week: [u8; 7],
    pub include_optional_holidays: bool,
    pub export_path: Option<PathBuf>,
    pub calendar_colors: BTreeMap<String, String>,
}

impl From<&UserSettings> for SettingsRow {
    fn from(settings: &UserSettings) -> Self {
        Self {
            schema_version: CURRENT_SCHEMA_VERSION,
            first_name: settings.first_name.clone(),
            last_name: settings.last_name.clone(),
            address: settings.address.clone(),
            department: settings.department.clone(),
            personnel_number: settings.personnel_number.clone(),
            class_name: settings.class_name.clone(),
            annual_entitlement_half_units: settings.annual_entitlement_half_units,
            workday_mask: settings.workday_mask,
            federal_state_code: settings.federal_state_code.clone(),
            student_mode: settings.student_mode,
            vocational_school_week: settings.vocational_school_week,
            include_optional_holidays: settings.include_optional_holidays,
            export_path: settings.export_path.clone(),
            calendar_colors: settings.calendar_colors.clone(),
        }
    }
}

impl TryFrom<SettingsRow> for UserSettings {
    type Error = StorageError;

    fn try_from(row: SettingsRow) -> Result<Self, Self::Error> {
        if row.vocational_school_week.iter().any(|value| *value > 2) {
            return Err(StorageError::InvalidSettings(
                "Berufsschulwerte müssen 0, 1 oder 2 sein.".into(),
            ));
        }
        Ok(Self {
            first_name: row.first_name,
            last_name: row.last_name,
            address: row.address,
            department: row.department,
            personnel_number: row.personnel_number,
            class_name: row.class_name,
            annual_entitlement_half_units: row.annual_entitlement_half_units,
            workday_mask: row.workday_mask,
            federal_state_code: row.federal_state_code,
            student_mode: row.student_mode,
            vocational_school_week: row.vocational_school_week,
            include_optional_holidays: row.include_optional_holidays,
            export_path: row.export_path,
            calendar_colors: row.calendar_colors,
        })
    }
}

pub struct Database {
    connection: Connection,
    path: Option<PathBuf>,
}

impl Database {
    pub fn open(path: impl AsRef<Path>) -> Result<Self, StorageError> {
        let path = path.as_ref().to_path_buf();
        if let Some(parent) = path
            .parent()
            .filter(|parent| !parent.as_os_str().is_empty())
        {
            fs::create_dir_all(parent).map_err(|source| StorageError::Io {
                path: parent.to_path_buf(),
                source,
            })?;
        }
        let connection = Connection::open(&path)?;
        Self::from_connection(connection, Some(path))
    }

    pub fn open_in_memory() -> Result<Self, StorageError> {
        Self::from_connection(Connection::open_in_memory()?, None)
    }

    fn from_connection(
        connection: Connection,
        path: Option<PathBuf>,
    ) -> Result<Self, StorageError> {
        connection.execute_batch("PRAGMA foreign_keys = ON;")?;
        let mut database = Self { connection, path };
        database.migrate()?;
        Ok(database)
    }

    pub fn path(&self) -> Option<&Path> {
        self.path.as_deref()
    }

    pub fn schema_version(&self) -> Result<i32, StorageError> {
        Ok(self
            .connection
            .pragma_query_value(None, "user_version", |row| row.get(0))?)
    }

    pub fn migrate(&mut self) -> Result<(), StorageError> {
        let mut version = self.schema_version()?;
        if version > CURRENT_SCHEMA_VERSION {
            return Err(StorageError::UnsupportedSchema {
                found: version,
                supported: CURRENT_SCHEMA_VERSION,
            });
        }

        while version < CURRENT_SCHEMA_VERSION {
            let next_version = version + 1;
            let transaction = self
                .connection
                .transaction_with_behavior(TransactionBehavior::Immediate)?;
            match next_version {
                1 => transaction.execute_batch(MIGRATION_1)?,
                2 => transaction.execute_batch(MIGRATION_2)?,
                _ => {
                    return Err(StorageError::UnsupportedSchema {
                        found: next_version,
                        supported: CURRENT_SCHEMA_VERSION,
                    });
                }
            }
            transaction.pragma_update(None, "user_version", next_version)?;
            transaction.commit()?;
            version = next_version;
        }
        Ok(())
    }

    pub fn with_transaction<T>(
        &mut self,
        operation: impl FnOnce(&Transaction<'_>) -> rusqlite::Result<T>,
    ) -> Result<T, StorageError> {
        let transaction = self
            .connection
            .transaction_with_behavior(TransactionBehavior::Immediate)?;
        let value = operation(&transaction)?;
        transaction.commit()?;
        Ok(value)
    }

    pub fn integrity_check(&self) -> Result<(), StorageError> {
        check_integrity(&self.connection)
    }

    pub fn backup_to_new(&self, destination: impl AsRef<Path>) -> Result<(), StorageError> {
        let destination = destination.as_ref();
        let reservation = reserve_new_file(destination)?;
        drop(reservation);

        match self
            .connection
            .backup("main", destination, None::<fn(Progress)>)
        {
            Ok(()) => match validate_database_file(destination) {
                Ok(()) => Ok(()),
                Err(error) => {
                    remove_failed_output(destination)?;
                    Err(error)
                }
            },
            Err(error) => {
                remove_failed_output(destination)?;
                Err(StorageError::Sqlite(error))
            }
        }
    }

    pub fn restore_backup_to_new(
        backup_path: impl AsRef<Path>,
        destination: impl AsRef<Path>,
    ) -> Result<(), StorageError> {
        let backup_path = backup_path.as_ref();
        let destination = destination.as_ref();
        validate_database_file(backup_path)?;

        let reservation = reserve_new_file(destination)?;
        drop(reservation);

        let source = Connection::open_with_flags(
            backup_path,
            OpenFlags::SQLITE_OPEN_READ_ONLY | OpenFlags::SQLITE_OPEN_URI,
        )?;
        match source.backup("main", destination, None::<fn(Progress)>) {
            Ok(()) => match validate_database_file(destination) {
                Ok(()) => Ok(()),
                Err(error) => {
                    remove_failed_output(destination)?;
                    Err(error)
                }
            },
            Err(error) => {
                remove_failed_output(destination)?;
                Err(StorageError::Sqlite(error))
            }
        }
    }

    /// Ersetzt den Inhalt dieser Datenbank durch eine Sicherung (REQ-DATA-004).
    ///
    /// Vorher wird der aktuelle Stand nach `safety_backup` gesichert; dieser Pfad
    /// darf noch nicht existieren. Ist die Sicherung beschädigt oder neuer als
    /// unterstützt, bleibt alles unverändert. Scheitert die Wiederherstellung
    /// unterwegs, wird der vorherige Stand aus der Sicherheitskopie zurückgeholt.
    /// Ältere Schemata werden nach der Wiederherstellung aktualisiert.
    pub fn restore_from_backup(
        &mut self,
        backup_path: impl AsRef<Path>,
        safety_backup: impl AsRef<Path>,
    ) -> Result<(), StorageError> {
        let backup_path = backup_path.as_ref();
        let safety_backup = safety_backup.as_ref();
        if let Some(current) = &self.path {
            let same_file = match (fs::canonicalize(current), fs::canonicalize(backup_path)) {
                (Ok(current), Ok(backup)) => current == backup,
                _ => false,
            };
            if same_file {
                return Err(StorageError::InvalidBackup(
                    "Die laufende Datenbank kann nicht als Sicherung gewählt werden.".into(),
                ));
            }
        }
        validate_database_file(backup_path)?;
        self.backup_to_new(safety_backup)?;

        let outcome = self
            .connection
            .restore("main", backup_path, None::<fn(Progress)>)
            .map_err(StorageError::from)
            .and_then(|()| self.migrate())
            .and_then(|()| self.integrity_check());
        if let Err(error) = outcome {
            let rollback = self
                .connection
                .restore("main", safety_backup, None::<fn(Progress)>)
                .map_err(StorageError::from)
                .and_then(|()| self.migrate());
            return Err(match rollback {
                Ok(()) => error,
                Err(rollback) => StorageError::Integrity(format!(
                    "Die Wiederherstellung ist fehlgeschlagen ({error}) und der vorherige Stand konnte nicht zurückgeholt werden ({rollback}). Er liegt gesichert unter {}.",
                    safety_backup.display()
                )),
            });
        }
        Ok(())
    }

    /// Ersetzt die Schulferien eines Bundeslands in einer Transaktion (REQ-HOL-003).
    /// Andere Bundesländer bleiben unverändert.
    pub fn replace_school_holidays(
        &mut self,
        import: &SchoolHolidayImport,
    ) -> Result<(), StorageError> {
        if import.state_code.trim().is_empty() || import.breaks.is_empty() {
            return Err(StorageError::InvalidCalendar(
                "Bundesland oder Ferienzeiträume fehlen.".into(),
            ));
        }
        if let Some(bad) = import
            .breaks
            .iter()
            .find(|item| item.end_date_exclusive <= item.start_date)
        {
            return Err(StorageError::InvalidCalendar(format!(
                "Der Zeitraum „{}“ endet nicht nach seinem Beginn.",
                bad.summary
            )));
        }
        let transaction = self
            .connection
            .transaction_with_behavior(TransactionBehavior::Immediate)?;
        transaction.execute(
            "DELETE FROM school_breaks WHERE state_code = ?1",
            [&import.state_code],
        )?;
        transaction.execute(
            "INSERT INTO calendar_sources (source_type, state_code, source_url, fetched_at_utc, latest_event_stamp_utc, content_sha256, data_version)
             VALUES ('school_vacations', ?1, ?2, ?3, ?4, ?5, ?6)
             ON CONFLICT (source_type, state_code) DO UPDATE SET
                source_url = excluded.source_url,
                fetched_at_utc = excluded.fetched_at_utc,
                latest_event_stamp_utc = excluded.latest_event_stamp_utc,
                content_sha256 = excluded.content_sha256,
                data_version = excluded.data_version",
            params![
                import.state_code,
                import.source_url,
                import.imported_at_utc.to_rfc3339(),
                import.latest_event_stamp_utc.map(|stamp| stamp.to_rfc3339()),
                import.content_sha256,
                import.file_name
            ],
        )?;
        for item in &import.breaks {
            transaction.execute(
                "INSERT OR IGNORE INTO school_breaks (state_code, start_date, end_date_exclusive, summary, source_url, data_version)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
                params![
                    import.state_code,
                    item.start_date.to_string(),
                    item.end_date_exclusive.to_string(),
                    item.summary,
                    import.source_url,
                    import.file_name
                ],
            )?;
        }
        transaction.commit()?;
        Ok(())
    }

    /// Gespeicherte Schulferien eines Bundeslands, `None` ohne Import.
    pub fn load_school_holidays(
        &self,
        state_code: &str,
    ) -> Result<Option<SchoolHolidayImport>, StorageError> {
        let source = self
            .connection
            .query_row(
                "SELECT source_url, fetched_at_utc, latest_event_stamp_utc, content_sha256, data_version
                 FROM calendar_sources WHERE source_type = 'school_vacations' AND state_code = ?1",
                [state_code],
                |row| {
                    Ok((
                        row.get::<_, String>(0)?,
                        row.get::<_, String>(1)?,
                        row.get::<_, Option<String>>(2)?,
                        row.get::<_, String>(3)?,
                        row.get::<_, String>(4)?,
                    ))
                },
            )
            .optional()?;
        let Some((source_url, imported_at, latest_stamp, content_sha256, file_name)) = source
        else {
            return Ok(None);
        };
        let mut statement = self.connection.prepare(
            "SELECT start_date, end_date_exclusive, summary FROM school_breaks
             WHERE state_code = ?1 ORDER BY start_date, end_date_exclusive",
        )?;
        let mut rows = statement.query([state_code])?;
        let mut breaks = Vec::new();
        while let Some(row) = rows.next()? {
            breaks.push(SchoolBreakRecord {
                start_date: parse_stored_date(&row.get::<_, String>(0)?)?,
                end_date_exclusive: parse_stored_date(&row.get::<_, String>(1)?)?,
                summary: row.get(2)?,
            });
        }
        Ok(Some(SchoolHolidayImport {
            state_code: state_code.to_owned(),
            source_url,
            file_name,
            imported_at_utc: parse_stored_timestamp(&imported_at)?,
            latest_event_stamp_utc: latest_stamp
                .map(|stamp| parse_stored_timestamp(&stamp))
                .transpose()?,
            content_sha256,
            breaks,
        }))
    }

    pub fn save_settings(&self, settings: &UserSettings) -> Result<(), StorageError> {
        write_settings(&self.connection, settings)
    }

    pub fn load_settings(&self) -> Result<Option<UserSettings>, StorageError> {
        let stored = self
            .connection
            .query_row(
                "SELECT schema_version, first_name, last_name, address, department,
                        personnel_number, class_name, annual_entitlement_half_units,
                        workday_mask, federal_state_code, student_mode,
                        vocational_school_week, include_optional_holidays, export_path,
                        calendar_colors_json
                 FROM settings WHERE singleton = 1",
                [],
                |row| {
                    let schedule: Vec<u8> = row.get(11)?;
                    Ok((
                        row.get::<_, i32>(0)?,
                        row.get::<_, String>(1)?,
                        row.get::<_, String>(2)?,
                        row.get::<_, Option<String>>(3)?,
                        row.get::<_, Option<String>>(4)?,
                        row.get::<_, Option<String>>(5)?,
                        row.get::<_, Option<String>>(6)?,
                        row.get::<_, u32>(7)?,
                        row.get::<_, u8>(8)?,
                        row.get::<_, Option<String>>(9)?,
                        row.get::<_, bool>(10)?,
                        schedule,
                        row.get::<_, bool>(12)?,
                        row.get::<_, Option<String>>(13)?,
                        row.get::<_, String>(14)?,
                    ))
                },
            )
            .optional()?;

        stored
            .map(
                |(
                    schema_version,
                    first_name,
                    last_name,
                    address,
                    department,
                    personnel_number,
                    class_name,
                    annual_entitlement_half_units,
                    workday_mask,
                    federal_state_code,
                    student_mode,
                    schedule,
                    include_optional_holidays,
                    export_path,
                    calendar_colors_json,
                )| {
                    if schema_version > CURRENT_SCHEMA_VERSION {
                        return Err(StorageError::UnsupportedSchema {
                            found: schema_version,
                            supported: CURRENT_SCHEMA_VERSION,
                        });
                    }
                    if workday_mask > 0b0111_1111 {
                        return Err(StorageError::InvalidSettings(
                            "Die Arbeitswochenmaske enthält ungültige Tage.".into(),
                        ));
                    }
                    let vocational_school_week: [u8; 7] = schedule.try_into().map_err(|_| {
                        StorageError::InvalidSettings(
                            "Der Berufsschulplan muss genau sieben Werte enthalten.".into(),
                        )
                    })?;
                    let calendar_colors = serde_json::from_str(&calendar_colors_json)
                        .map_err(|error| StorageError::InvalidSettings(error.to_string()))?;
                    let record = SettingsRow {
                        schema_version,
                        first_name,
                        last_name,
                        address,
                        department,
                        personnel_number,
                        class_name,
                        annual_entitlement_half_units,
                        workday_mask,
                        federal_state_code,
                        student_mode,
                        vocational_school_week,
                        include_optional_holidays,
                        export_path: export_path.map(PathBuf::from),
                        calendar_colors,
                    };
                    UserSettings::try_from(record)
                },
            )
            .transpose()
    }
}

impl SettingsRepository for Database {
    type Error = StorageError;

    fn load_settings(&self) -> Result<Option<UserSettings>, Self::Error> {
        Database::load_settings(self)
    }

    fn save_settings(&self, settings: &UserSettings) -> Result<(), Self::Error> {
        Database::save_settings(self, settings)
    }
}

impl RequestRepository for Database {
    type Error = StorageError;

    fn create_draft(&mut self, request: &DraftRequest) -> Result<(), Self::Error> {
        if request.request_id.trim().is_empty()
            || request.start_date > request.end_date
            || request.start_date.year() != request.end_date.year()
            || request.requested_half_units == 0
            || (request.start_date == request.end_date
                && request.start_half_day
                && request.end_half_day)
        {
            return Err(StorageError::InvalidRequest(
                "Ungültiger Zeitraum oder Urlaubsverbrauch.".into(),
            ));
        }
        let aza_dates: BTreeSet<_> = request.aza_dates.iter().copied().collect();
        if aza_dates.len() != request.aza_dates.len()
            || aza_dates
                .iter()
                .any(|date| *date < request.start_date || *date > request.end_date)
        {
            return Err(StorageError::InvalidRequest(
                "AZA-Daten müssen eindeutig und innerhalb des Zeitraums liegen.".into(),
            ));
        }
        let transaction = self
            .connection
            .transaction_with_behavior(TransactionBehavior::Immediate)?;
        let timestamp = request.created_at_utc.to_rfc3339();
        transaction.execute(
            "INSERT INTO vacation_requests (request_id, schema_version, year, start_date, end_date, start_half_day, end_half_day, requested_half_units, status, created_at_utc, updated_at_utc)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, 'Draft', ?9, ?9)",
            params![request.request_id, CURRENT_SCHEMA_VERSION, request.start_date.year(), request.start_date.to_string(), request.end_date.to_string(), request.start_half_day, request.end_half_day, request.requested_half_units, timestamp],
        )?;
        for date in aza_dates {
            transaction.execute(
                "INSERT INTO request_aza_dates (request_id, date) VALUES (?1, ?2)",
                params![request.request_id, date.to_string()],
            )?;
        }
        transaction.execute(
            "INSERT INTO request_status_events (event_id, request_id, previous_status, new_status, occurred_at_utc) VALUES (?1, ?2, NULL, 'Draft', ?3)",
            params![format!("created:{}", request.request_id), request.request_id, timestamp],
        )?;
        transaction.commit()?;
        Ok(())
    }

    fn load_drafts(&self) -> Result<Vec<DraftRequest>, Self::Error> {
        let mut statement = self.connection.prepare(
            "SELECT request_id, start_date, end_date, start_half_day, end_half_day, requested_half_units, created_at_utc FROM vacation_requests WHERE status = 'Draft' ORDER BY start_date DESC, request_id"
        )?;
        let rows = statement.query_map([], |row| {
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, String>(1)?,
                row.get::<_, String>(2)?,
                row.get::<_, bool>(3)?,
                row.get::<_, bool>(4)?,
                row.get::<_, u32>(5)?,
                row.get::<_, String>(6)?,
            ))
        })?;
        let mut drafts = Vec::new();
        for row in rows {
            let (
                request_id,
                start_date,
                end_date,
                start_half_day,
                end_half_day,
                requested_half_units,
                created_at_utc,
            ) = row?;
            let parse_date = |value: &str| {
                NaiveDate::parse_from_str(value, "%Y-%m-%d")
                    .map_err(|error| StorageError::Integrity(error.to_string()))
            };
            let mut aza_statement = self.connection.prepare(
                "SELECT date FROM request_aza_dates WHERE request_id = ?1 ORDER BY date",
            )?;
            let aza_rows = aza_statement.query_map([&request_id], |row| row.get::<_, String>(0))?;
            let mut aza_dates = Vec::new();
            for date in aza_rows {
                aza_dates.push(parse_date(&date?)?);
            }
            drafts.push(DraftRequest {
                request_id,
                start_date: parse_date(&start_date)?,
                end_date: parse_date(&end_date)?,
                start_half_day,
                end_half_day,
                requested_half_units,
                created_at_utc: DateTime::parse_from_rfc3339(&created_at_utc)
                    .map_err(|error| StorageError::Integrity(error.to_string()))?
                    .with_timezone(&Utc),
                aza_dates,
            });
        }
        Ok(drafts)
    }

    fn change_status(
        &mut self,
        request_id: &str,
        new_status: RequestStatus,
        occurred_at_utc: DateTime<Utc>,
        rejection_reason: Option<&str>,
    ) -> Result<(), Self::Error> {
        let transaction = self
            .connection
            .transaction_with_behavior(TransactionBehavior::Immediate)?;
        apply_status_change(
            &transaction,
            request_id,
            new_status,
            occurred_at_utc,
            rejection_reason,
        )?;
        transaction.commit()?;
        Ok(())
    }

    fn load_requests(&self, include_deleted: bool) -> Result<Vec<StoredRequest>, Self::Error> {
        let mut statement = self.connection.prepare(
            "SELECT request_id, start_date, end_date, start_half_day, end_half_day, requested_half_units, status, rejection_reason, archived_at_utc, created_at_utc, updated_at_utc,
                    (SELECT previous_status FROM request_status_events
                      WHERE request_id = vacation_requests.request_id AND new_status = 'Archived'
                        AND (previous_status IS NULL OR previous_status <> 'Deleted')
                      ORDER BY rowid DESC LIMIT 1)
             FROM vacation_requests
             WHERE ?1 OR status <> 'Deleted'
             ORDER BY start_date DESC, request_id",
        )?;
        let mut aza_statement = self
            .connection
            .prepare("SELECT date FROM request_aza_dates WHERE request_id = ?1 ORDER BY date")?;
        let mut rows = statement.query([include_deleted])?;
        let mut requests = Vec::new();
        while let Some(row) = rows.next()? {
            let request_id: String = row.get(0)?;
            let aza_rows = aza_statement.query_map([&request_id], |row| row.get::<_, String>(0))?;
            let mut aza_dates = Vec::new();
            for date in aza_rows {
                aza_dates.push(parse_stored_date(&date?)?);
            }
            let status = parse_stored_status(&row.get::<_, String>(6)?)?;
            let rejection_reason: Option<String> = row.get(7)?;
            // Ein archivierter Antrag zählt weiter als genehmigt, wenn er vor dem
            // Archivieren genehmigt war. Fehlt bei übernommenen Altdaten der
            // Statusverlauf, entscheidet der Ablehnungsgrund.
            let counts_as_approved = match status {
                RequestStatus::Approved => true,
                RequestStatus::Archived => match row.get::<_, Option<String>>(11)? {
                    Some(previous) => parse_stored_status(&previous)? == RequestStatus::Approved,
                    None => rejection_reason.is_none(),
                },
                _ => false,
            };
            requests.push(StoredRequest {
                start_date: parse_stored_date(&row.get::<_, String>(1)?)?,
                end_date: parse_stored_date(&row.get::<_, String>(2)?)?,
                start_half_day: row.get(3)?,
                end_half_day: row.get(4)?,
                requested_half_units: row.get(5)?,
                status,
                rejection_reason,
                counts_as_approved,
                archived_at_utc: row
                    .get::<_, Option<String>>(8)?
                    .map(|value| parse_stored_timestamp(&value))
                    .transpose()?,
                created_at_utc: parse_stored_timestamp(&row.get::<_, String>(9)?)?,
                updated_at_utc: parse_stored_timestamp(&row.get::<_, String>(10)?)?,
                aza_dates,
                request_id,
            });
        }
        Ok(requests)
    }

    fn load_status_events(&self, request_id: &str) -> Result<Vec<StatusEvent>, Self::Error> {
        let exists: bool = self.connection.query_row(
            "SELECT EXISTS (SELECT 1 FROM vacation_requests WHERE request_id = ?1)",
            [request_id],
            |row| row.get(0),
        )?;
        if !exists {
            return Err(StorageError::RequestNotFound(request_id.to_owned()));
        }
        let mut statement = self.connection.prepare(
            "SELECT previous_status, new_status, occurred_at_utc, rejection_reason
             FROM request_status_events WHERE request_id = ?1 ORDER BY rowid",
        )?;
        let mut rows = statement.query([request_id])?;
        let mut events = Vec::new();
        while let Some(row) = rows.next()? {
            events.push(StatusEvent {
                previous_status: row
                    .get::<_, Option<String>>(0)?
                    .map(|value| parse_stored_status(&value))
                    .transpose()?,
                new_status: parse_stored_status(&row.get::<_, String>(1)?)?,
                occurred_at_utc: parse_stored_timestamp(&row.get::<_, String>(2)?)?,
                rejection_reason: row.get(3)?,
            });
        }
        Ok(events)
    }

    fn record_export(
        &mut self,
        request_id: &str,
        file_path: &Path,
        content_sha256: Option<&str>,
        exported_at_utc: DateTime<Utc>,
    ) -> Result<(), Self::Error> {
        let path_text = file_path
            .to_str()
            .filter(|path| !path.is_empty())
            .ok_or_else(|| {
                StorageError::InvalidRequest("Der Exportpfad kann nicht gespeichert werden.".into())
            })?;
        let transaction = self
            .connection
            .transaction_with_behavior(TransactionBehavior::Immediate)?;
        let current = stored_status(&transaction, request_id)?;
        if current == RequestStatus::Deleted {
            return Err(StorageError::InvalidRequest(
                "Für einen gelöschten Antrag kann kein Export festgehalten werden.".into(),
            ));
        }
        let timestamp = exported_at_utc.to_rfc3339();
        let sequence: i64 = transaction.query_row(
            "SELECT count(*) FROM export_records WHERE request_id = ?1",
            [request_id],
            |row| row.get(0),
        )?;
        transaction.execute(
            "INSERT INTO export_records (export_id, request_id, file_path, content_sha256, exported_at_utc)
             VALUES (?1, ?2, ?3, ?4, ?5)",
            params![
                format!("export:{request_id}:{}:{timestamp}", sequence + 1),
                request_id,
                path_text,
                content_sha256,
                timestamp
            ],
        )?;
        if current == RequestStatus::Draft {
            apply_status_change(
                &transaction,
                request_id,
                RequestStatus::Exported,
                exported_at_utc,
                None,
            )?;
        }
        transaction.commit()?;
        Ok(())
    }

    fn load_exports(&self, request_id: &str) -> Result<Vec<ExportRecord>, Self::Error> {
        let mut statement = self.connection.prepare(
            "SELECT file_path, content_sha256, exported_at_utc
             FROM export_records WHERE request_id = ?1 ORDER BY exported_at_utc, rowid",
        )?;
        let mut rows = statement.query([request_id])?;
        let mut exports = Vec::new();
        while let Some(row) = rows.next()? {
            exports.push(ExportRecord {
                file_path: PathBuf::from(row.get::<_, String>(0)?),
                content_sha256: row.get(1)?,
                exported_at_utc: parse_stored_timestamp(&row.get::<_, String>(2)?)?,
            });
        }
        Ok(exports)
    }
}

fn stored_status(
    transaction: &Transaction<'_>,
    request_id: &str,
) -> Result<RequestStatus, StorageError> {
    let stored: Option<String> = transaction
        .query_row(
            "SELECT status FROM vacation_requests WHERE request_id = ?1",
            [request_id],
            |row| row.get(0),
        )
        .optional()?;
    parse_stored_status(
        &stored.ok_or_else(|| StorageError::RequestNotFound(request_id.to_owned()))?,
    )
}

fn apply_status_change(
    transaction: &Transaction<'_>,
    request_id: &str,
    new_status: RequestStatus,
    occurred_at_utc: DateTime<Utc>,
    rejection_reason: Option<&str>,
) -> Result<(), StorageError> {
    let current = stored_status(transaction, request_id)?;
    let last_previous = |entered: RequestStatus| -> Result<Option<RequestStatus>, StorageError> {
        let previous: Option<Option<String>> = transaction
            .query_row(
                "SELECT previous_status FROM request_status_events
                 WHERE request_id = ?1 AND new_status = ?2
                   AND (?2 <> 'Archived' OR previous_status IS NULL OR previous_status <> 'Deleted')
                 ORDER BY rowid DESC LIMIT 1",
                params![request_id, entered.as_str()],
                |row| row.get(0),
            )
            .optional()?;
        previous
            .flatten()
            .map(|value| parse_stored_status(&value))
            .transpose()
    };
    let previous_status = match current {
        RequestStatus::Deleted => last_previous(RequestStatus::Deleted)?,
        RequestStatus::Archived => match last_previous(RequestStatus::Archived)? {
            Some(previous) => Some(previous),
            // Übernommene Altfälle ohne Verlauf: wie bei `counts_as_approved`
            // entscheidet der Ablehnungsgrund.
            None => {
                let reason: Option<String> = transaction.query_row(
                    "SELECT rejection_reason FROM vacation_requests WHERE request_id = ?1",
                    [request_id],
                    |row| row.get(0),
                )?;
                Some(if reason.is_some() {
                    RequestStatus::Rejected
                } else {
                    RequestStatus::Approved
                })
            }
        },
        _ => None,
    };
    validate_status_transition(current, previous_status, new_status, rejection_reason)?;

    // Wiederherstellen und Ent-Archivieren setzen keinen neuen Ablehnungsgrund.
    let restoring = current == RequestStatus::Deleted
        || (current == RequestStatus::Archived && new_status != RequestStatus::Deleted);
    let unarchiving = current == RequestStatus::Archived && new_status != RequestStatus::Deleted;
    let reason = rejection_reason
        .map(str::trim)
        .filter(|reason| !reason.is_empty() && !restoring);
    let timestamp = occurred_at_utc.to_rfc3339();
    transaction.execute(
        "UPDATE vacation_requests
         SET status = ?2,
             updated_at_utc = ?3,
             rejection_reason = COALESCE(?4, rejection_reason),
             archived_at_utc = CASE WHEN ?5 THEN ?3 WHEN ?6 THEN NULL ELSE archived_at_utc END
         WHERE request_id = ?1",
        params![
            request_id,
            new_status.as_str(),
            timestamp,
            reason,
            new_status == RequestStatus::Archived && current != RequestStatus::Deleted,
            unarchiving
        ],
    )?;
    let sequence: i64 = transaction.query_row(
        "SELECT count(*) FROM request_status_events WHERE request_id = ?1",
        [request_id],
        |row| row.get(0),
    )?;
    transaction.execute(
        "INSERT INTO request_status_events (event_id, request_id, previous_status, new_status, occurred_at_utc, rejection_reason)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
        params![
            format!("status:{request_id}:{}:{timestamp}", sequence + 1),
            request_id,
            current.as_str(),
            new_status.as_str(),
            timestamp,
            reason
        ],
    )?;
    Ok(())
}

fn parse_stored_status(value: &str) -> Result<RequestStatus, StorageError> {
    RequestStatus::parse(value)
        .ok_or_else(|| StorageError::Integrity(format!("Unbekannter Antragsstatus {value:?}.")))
}

fn parse_stored_date(value: &str) -> Result<NaiveDate, StorageError> {
    NaiveDate::parse_from_str(value, "%Y-%m-%d")
        .map_err(|error| StorageError::Integrity(format!("Ungültiges Datum {value:?}: {error}")))
}

fn parse_stored_timestamp(value: &str) -> Result<DateTime<Utc>, StorageError> {
    DateTime::parse_from_rfc3339(value)
        .map(|timestamp| timestamp.with_timezone(&Utc))
        .map_err(|error| {
            StorageError::Integrity(format!("Ungültiger Zeitstempel {value:?}: {error}"))
        })
}

impl MigrationRepository for Database {
    type Error = StorageError;

    fn apply_legacy_import(
        &mut self,
        batch: &LegacyImportBatch,
    ) -> Result<ImportCommitReport, Self::Error> {
        validate_import_batch(batch)?;
        let transaction = self
            .connection
            .transaction_with_behavior(TransactionBehavior::Immediate)?;

        let existing_status = transaction
            .query_row(
                "SELECT status, source_sha256 FROM migration_runs WHERE migration_id = ?1",
                [&batch.migration_id],
                |row| Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?)),
            )
            .optional()?;
        match existing_status.as_ref() {
            Some((status, source_sha256))
                if status == "committed" && source_sha256 == &batch.source_sha256 =>
            {
                return Ok(ImportCommitReport {
                    already_imported: true,
                    ..ImportCommitReport::default()
                });
            }
            Some((_, source_sha256)) if source_sha256 != &batch.source_sha256 => {
                return Err(StorageError::MigrationConflict(
                    "Die Migration-ID ist bereits mit einer anderen Quell-Prüfsumme verknüpft."
                        .into(),
                ));
            }
            Some((status, _)) => {
                return Err(StorageError::MigrationConflict(format!(
                    "Migration-ID {} hat bereits Status {status}.",
                    batch.migration_id
                )));
            }
            None => {}
        }

        let mut report = ImportCommitReport::default();
        if let Some(settings) = &batch.settings {
            let settings_exist: bool = transaction.query_row(
                "SELECT EXISTS(SELECT 1 FROM settings WHERE singleton = 1)",
                [],
                |row| row.get(0),
            )?;
            if settings_exist {
                return Err(StorageError::MigrationConflict(
                    "Einstellungen sind bereits vorhanden; Import überschreibt sie nicht.".into(),
                ));
            }
            write_settings(&transaction, settings)?;
            report.settings_imported = true;
        }

        let mut request_ids = BTreeSet::new();
        for request in &batch.requests {
            if !request_ids.insert(request.request_id.as_str()) {
                return Err(StorageError::MigrationConflict(format!(
                    "Der Migrationsbatch enthält die Request-ID {} mehrfach.",
                    request.request_id
                )));
            }
            if request.request_id.trim().is_empty()
                || request.year != request.start_date.year()
                || request.end_date < request.start_date
            {
                return Err(StorageError::InvalidMigrationBatch(format!(
                    "Antrag {} hat ungültige Pflichtfelder oder Datumswerte.",
                    request.request_id
                )));
            }
            let already_exists: bool = transaction.query_row(
                "SELECT EXISTS(SELECT 1 FROM vacation_requests WHERE request_id = ?1)",
                [&request.request_id],
                |row| row.get(0),
            )?;
            if already_exists {
                return Err(StorageError::MigrationConflict(format!(
                    "Die Request-ID {} existiert bereits.",
                    request.request_id
                )));
            }
            transaction.execute(
                "INSERT INTO vacation_requests (
                    request_id, schema_version, year, start_date, end_date,
                    start_half_day, end_half_day, requested_half_units, status,
                    rejection_reason, archived_at_utc, created_at_utc, updated_at_utc
                ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13)",
                params![
                    request.request_id,
                    CURRENT_SCHEMA_VERSION,
                    request.year,
                    request.start_date.format("%Y-%m-%d").to_string(),
                    request.end_date.format("%Y-%m-%d").to_string(),
                    request.start_half_day,
                    request.end_half_day,
                    request.requested_half_units,
                    request.status.as_str(),
                    request.rejection_reason,
                    request.archived_at_utc.map(|value| value.to_rfc3339()),
                    request.created_at_utc.to_rfc3339(),
                    request.updated_at_utc.to_rfc3339(),
                ],
            )?;
            let mut aza_dates = BTreeSet::new();
            for date in &request.aza_dates {
                if !aza_dates.insert(*date) {
                    return Err(StorageError::InvalidMigrationBatch(format!(
                        "Antrag {} enthält das AZA-Datum {} mehrfach.",
                        request.request_id, date
                    )));
                }
                if *date < request.start_date || *date > request.end_date {
                    return Err(StorageError::InvalidMigrationBatch(format!(
                        "AZA-Datum {} liegt außerhalb des Antrags {}.",
                        date, request.request_id
                    )));
                }
                transaction.execute(
                    "INSERT INTO request_aza_dates (request_id, date) VALUES (?1, ?2)",
                    params![request.request_id, date.format("%Y-%m-%d").to_string()],
                )?;
            }
            report.requests_imported += 1;
        }

        let mut event_ids = BTreeSet::new();
        for event in &batch.status_events {
            if !event_ids.insert(event.event_id.as_str()) {
                return Err(StorageError::MigrationConflict(format!(
                    "Der Migrationsbatch enthält die Event-ID {} mehrfach.",
                    event.event_id
                )));
            }
            if event.event_id.trim().is_empty() || event.request_id.trim().is_empty() {
                return Err(StorageError::InvalidMigrationBatch(
                    "Statusereignis enthält keine Event- oder Request-ID.".into(),
                ));
            }
            transaction.execute(
                "INSERT INTO request_status_events (
                    event_id, request_id, previous_status, new_status,
                    occurred_at_utc, rejection_reason
                ) VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
                params![
                    event.event_id,
                    event.request_id,
                    event.previous_status.map(|status| status.as_str()),
                    event.new_status.as_str(),
                    event.occurred_at_utc.to_rfc3339(),
                    event.rejection_reason,
                ],
            )?;
            report.status_events_imported += 1;
        }

        let mut export_ids = BTreeSet::new();
        for export in &batch.exports {
            if !export_ids.insert(export.export_id.as_str()) {
                return Err(StorageError::MigrationConflict(format!(
                    "Der Migrationsbatch enthält die Export-ID {} mehrfach.",
                    export.export_id
                )));
            }
            transaction.execute(
                "INSERT INTO export_records (
                    export_id, request_id, file_path, content_sha256, exported_at_utc
                ) VALUES (?1, ?2, ?3, ?4, ?5)",
                params![
                    export.export_id,
                    export.request_id,
                    export.file_path.to_string_lossy(),
                    export.content_sha256,
                    export.exported_at_utc.to_rfc3339(),
                ],
            )?;
            report.exports_imported += 1;
        }

        let records_read = i64::try_from(batch.records_read).map_err(|_| {
            StorageError::InvalidMigrationBatch("Zu viele Datensätze im Importbatch.".into())
        })?;
        let records_ready = i64::try_from(batch.records_ready).map_err(|_| {
            StorageError::InvalidMigrationBatch("Zu viele Datensätze im Importbatch.".into())
        })?;
        let records_skipped = i64::try_from(batch.records_skipped).map_err(|_| {
            StorageError::InvalidMigrationBatch("Zu viele Datensätze im Importbatch.".into())
        })?;
        let report_json = format!(
            "{{\"settings_imported\":{},\"requests_imported\":{},\"status_events_imported\":{},\"exports_imported\":{},\"records_ready\":{},\"records_skipped\":{}}}",
            report.settings_imported,
            report.requests_imported,
            report.status_events_imported,
            report.exports_imported,
            batch.records_ready,
            batch.records_skipped
        );
        transaction.execute(
            "INSERT INTO migration_runs (
                migration_id, source_path, source_sha256, started_at_utc,
                completed_at_utc, status, records_read, records_imported,
                records_skipped, records_failed, report_json
            ) VALUES (?1, ?2, ?3, ?4, ?5, 'committed', ?6, ?7, ?8, 0, ?9)",
            params![
                batch.migration_id,
                batch.source_path.to_string_lossy(),
                batch.source_sha256,
                batch.started_at_utc.to_rfc3339(),
                batch.committed_at_utc.to_rfc3339(),
                records_read,
                records_ready,
                records_skipped,
                report_json,
            ],
        )?;

        transaction.commit()?;
        Ok(report)
    }
}

fn validate_settings(settings: &UserSettings) -> Result<(), StorageError> {
    if settings.workday_mask > 0b0111_1111 {
        return Err(StorageError::InvalidSettings(
            "Die Arbeitswochenmaske enthält ungültige Tage.".into(),
        ));
    }
    if settings
        .vocational_school_week
        .iter()
        .any(|value| *value > 2)
    {
        return Err(StorageError::InvalidSettings(
            "Berufsschulwerte müssen 0, 1 oder 2 sein.".into(),
        ));
    }
    Ok(())
}

fn validate_import_batch(batch: &LegacyImportBatch) -> Result<(), StorageError> {
    if batch.migration_id.trim().is_empty() || batch.source_format.trim().is_empty() {
        return Err(StorageError::InvalidMigrationBatch(
            "Migration-ID und Quellformat müssen angegeben sein.".into(),
        ));
    }
    if batch.source_sha256.len() != 64
        || !batch
            .source_sha256
            .bytes()
            .all(|byte| byte.is_ascii_hexdigit())
    {
        return Err(StorageError::InvalidMigrationBatch(
            "Die Quell-Prüfsumme muss eine SHA-256-Hexsumme sein.".into(),
        ));
    }
    if batch.migration_id != format!("{}:{}", batch.source_format, batch.source_sha256) {
        return Err(StorageError::InvalidMigrationBatch(
            "Die Migration-ID muss aus Quellformat und Quell-Prüfsumme gebildet sein.".into(),
        ));
    }
    if batch.committed_at_utc < batch.started_at_utc {
        return Err(StorageError::InvalidMigrationBatch(
            "Der Abschlusszeitpunkt liegt vor dem Startzeitpunkt.".into(),
        ));
    }
    if batch.records_ready == 0
        || batch.records_failed != 0
        || batch.records_ready.checked_add(batch.records_skipped) != Some(batch.records_read)
        || (batch.records_skipped > 0 && !batch.skipped_records_confirmed)
    {
        return Err(StorageError::InvalidMigrationBatch(
            "Preview-Zähler oder Bestätigung übersprungener Datensätze sind ungültig.".into(),
        ));
    }
    if let Some(settings) = &batch.settings {
        validate_settings(settings)?;
    }
    Ok(())
}

fn write_settings(connection: &Connection, settings: &UserSettings) -> Result<(), StorageError> {
    validate_settings(settings)?;
    let calendar_colors = serde_json::to_string(&settings.calendar_colors)
        .map_err(|error| StorageError::InvalidSettings(error.to_string()))?;
    let vocational_school_week = settings.vocational_school_week.to_vec();
    let export_path = settings
        .export_path
        .as_ref()
        .map(|path| path.to_string_lossy().into_owned());

    connection.execute(
        "INSERT INTO settings (
            singleton, schema_version, first_name, last_name, address, department,
            personnel_number, class_name, annual_entitlement_half_units, workday_mask,
            federal_state_code, student_mode, vocational_school_week,
            include_optional_holidays, export_path, calendar_colors_json
        ) VALUES (1, ?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15)
        ON CONFLICT(singleton) DO UPDATE SET
            schema_version = excluded.schema_version,
            first_name = excluded.first_name,
            last_name = excluded.last_name,
            address = excluded.address,
            department = excluded.department,
            personnel_number = excluded.personnel_number,
            class_name = excluded.class_name,
            annual_entitlement_half_units = excluded.annual_entitlement_half_units,
            workday_mask = excluded.workday_mask,
            federal_state_code = excluded.federal_state_code,
            student_mode = excluded.student_mode,
            vocational_school_week = excluded.vocational_school_week,
            include_optional_holidays = excluded.include_optional_holidays,
            export_path = excluded.export_path,
            calendar_colors_json = excluded.calendar_colors_json",
        params![
            CURRENT_SCHEMA_VERSION,
            settings.first_name,
            settings.last_name,
            settings.address,
            settings.department,
            settings.personnel_number,
            settings.class_name,
            settings.annual_entitlement_half_units,
            settings.workday_mask,
            settings.federal_state_code,
            settings.student_mode,
            vocational_school_week,
            settings.include_optional_holidays,
            export_path,
            calendar_colors,
        ],
    )?;
    Ok(())
}

pub(super) fn reserve_new_file(path: &Path) -> Result<File, StorageError> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|source| StorageError::Io {
            path: parent.to_path_buf(),
            source,
        })?;
    }
    OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .map_err(|source| {
            if source.kind() == io::ErrorKind::AlreadyExists {
                StorageError::DestinationExists(path.to_path_buf())
            } else {
                StorageError::Io {
                    path: path.to_path_buf(),
                    source,
                }
            }
        })
}

pub(super) fn remove_failed_output(path: &Path) -> Result<(), StorageError> {
    fs::remove_file(path).map_err(|source| StorageError::Io {
        path: path.to_path_buf(),
        source,
    })
}

fn validate_database_file(path: &Path) -> Result<(), StorageError> {
    let connection = Connection::open_with_flags(
        path,
        OpenFlags::SQLITE_OPEN_READ_ONLY | OpenFlags::SQLITE_OPEN_URI,
    )?;
    check_integrity(&connection)?;
    let version: i32 = connection.pragma_query_value(None, "user_version", |row| row.get(0))?;
    if !(1..=CURRENT_SCHEMA_VERSION).contains(&version) {
        return Err(StorageError::UnsupportedSchema {
            found: version,
            supported: CURRENT_SCHEMA_VERSION,
        });
    }
    Ok(())
}

fn check_integrity(connection: &Connection) -> Result<(), StorageError> {
    let result: String = connection.query_row("PRAGMA quick_check", [], |row| row.get(0))?;
    if result != "ok" {
        return Err(StorageError::Integrity(result));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicU64, Ordering};

    use super::*;

    static NEXT_TEMP_ID: AtomicU64 = AtomicU64::new(1);

    struct TempDirectory(PathBuf);

    impl TempDirectory {
        fn new() -> Self {
            let id = NEXT_TEMP_ID.fetch_add(1, Ordering::Relaxed);
            let path = std::env::temp_dir()
                .join(format!("urlaubstool-storage-{}-{id}", std::process::id()));
            fs::create_dir_all(&path).expect("create temporary test directory");
            Self(path)
        }

        fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for TempDirectory {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn sample_settings() -> UserSettings {
        UserSettings {
            first_name: "Ada".into(),
            last_name: "Beispiel".into(),
            address: None,
            department: Some("Entwicklung".into()),
            personnel_number: Some("P-001".into()),
            class_name: Some("FIAE".into()),
            annual_entitlement_half_units: 60,
            workday_mask: 0b0001_1111,
            federal_state_code: Some("NW".into()),
            student_mode: true,
            vocational_school_week: [0, 0, 1, 0, 2, 0, 0],
            include_optional_holidays: true,
            export_path: Some(PathBuf::from("/tmp/Urlaub/Exports")),
            calendar_colors: [("workday".into(), "#2F7155".into())].into(),
        }
    }

    fn sample_import_batch() -> LegacyImportBatch {
        use chrono::DateTime;
        use urlaubstool_application::{
            MigratedExportRecord, MigratedRequest, MigratedStatusEvent, RequestStatus,
        };

        let created_at = DateTime::parse_from_rfc3339("2026-01-02T03:04:05+00:00")
            .expect("valid timestamp")
            .to_utc();
        let approved_at = DateTime::parse_from_rfc3339("2026-01-02T03:04:07+00:00")
            .expect("valid timestamp")
            .to_utc();
        let request_id = "550e8400-e29b-41d4-a716-446655440001".to_owned();
        LegacyImportBatch {
            migration_id: format!("ledger_csv_v1:{}", "a".repeat(64)),
            source_path: PathBuf::from("/legacy/ledger.csv"),
            source_sha256: "a".repeat(64),
            source_format: "ledger_csv_v1".into(),
            started_at_utc: created_at,
            committed_at_utc: approved_at,
            records_read: 3,
            records_ready: 3,
            records_skipped: 0,
            records_failed: 0,
            skipped_records_confirmed: false,
            settings: Some(sample_settings()),
            requests: vec![MigratedRequest {
                request_id: request_id.clone(),
                year: 2026,
                start_date: chrono::NaiveDate::from_ymd_opt(2026, 11, 9).expect("valid date"),
                end_date: chrono::NaiveDate::from_ymd_opt(2026, 11, 10).expect("valid date"),
                start_half_day: true,
                end_half_day: false,
                requested_half_units: 3,
                status: RequestStatus::Approved,
                rejection_reason: None,
                archived_at_utc: None,
                created_at_utc: created_at,
                updated_at_utc: approved_at,
                aza_dates: vec![chrono::NaiveDate::from_ymd_opt(2026, 11, 10).expect("valid date")],
            }],
            status_events: vec![
                MigratedStatusEvent {
                    event_id: "legacy-created-1".into(),
                    request_id: request_id.clone(),
                    previous_status: None,
                    new_status: RequestStatus::Draft,
                    occurred_at_utc: created_at,
                    rejection_reason: None,
                },
                MigratedStatusEvent {
                    event_id: "legacy-approved-1".into(),
                    request_id: request_id.clone(),
                    previous_status: Some(RequestStatus::Exported),
                    new_status: RequestStatus::Approved,
                    occurred_at_utc: approved_at,
                    rejection_reason: None,
                },
            ],
            exports: vec![MigratedExportRecord {
                export_id: "legacy-export-1".into(),
                request_id,
                file_path: PathBuf::from("/legacy/Urlaubsantrag.pdf"),
                content_sha256: None,
                exported_at_utc: created_at,
            }],
        }
    }

    #[test]
    fn req_data_001_003_schema_initialization_and_upgrades_are_idempotent() {
        let mut database = Database::open_in_memory().expect("open database");
        assert_eq!(
            database.schema_version().expect("schema version"),
            CURRENT_SCHEMA_VERSION
        );
        database.migrate().expect("repeat migration");
        database.migrate().expect("repeat migration again");
        assert_eq!(
            database.schema_version().expect("schema version"),
            CURRENT_SCHEMA_VERSION
        );

        let columns = database
            .connection
            .column_exists(None, "calendar_sources", "content_sha256")
            .expect("check v2 column");
        assert!(columns);
    }

    #[test]
    fn req_data_001_003_v1_database_upgrades_transactionally_to_v2() {
        let connection = Connection::open_in_memory().expect("open database");
        connection
            .execute_batch(MIGRATION_1)
            .expect("create v1 schema");
        connection
            .pragma_update(None, "user_version", 1)
            .expect("set v1");
        let database = Database::from_connection(connection, None).expect("upgrade database");

        assert_eq!(
            database.schema_version().expect("schema version"),
            CURRENT_SCHEMA_VERSION
        );
        assert!(database
            .connection
            .table_exists(None, "school_breaks")
            .expect("v2 table"));
    }

    #[test]
    fn req_data_001_newer_schema_is_rejected_without_downgrade() {
        let connection = Connection::open_in_memory().expect("open database");
        connection
            .pragma_update(None, "user_version", CURRENT_SCHEMA_VERSION + 1)
            .expect("set future schema");
        assert!(matches!(
            Database::from_connection(connection, None),
            Err(StorageError::UnsupportedSchema {
                found: 3,
                supported: 2
            })
        ));
    }

    #[test]
    fn req_data_002_settings_round_trip_use_separate_storage_model() {
        let database = Database::open_in_memory().expect("open database");
        let settings = sample_settings();
        database.save_settings(&settings).expect("save settings");
        assert_eq!(
            database.load_settings().expect("load settings"),
            Some(settings)
        );
    }

    #[test]
    fn req_data_002_invalid_settings_are_rejected_before_write() {
        let database = Database::open_in_memory().expect("open database");
        let mut settings = sample_settings();
        settings.vocational_school_week[2] = 3;

        assert!(matches!(
            database.save_settings(&settings),
            Err(StorageError::InvalidSettings(_))
        ));
        assert_eq!(
            database.load_settings().expect("settings remain empty"),
            None
        );
    }

    #[test]
    fn req_data_003_failed_transaction_rolls_back_all_rows() {
        let mut database = Database::open_in_memory().expect("open database");
        let result = database.with_transaction::<()>(|transaction| {
            transaction.execute(
                "INSERT INTO migration_runs (migration_id, source_path, source_sha256, started_at_utc, status) VALUES ('m1', 'legacy.csv', 'hash', '2026-11-10T00:00:00Z', 'preview')",
                [],
            )?;
            Err(rusqlite::Error::InvalidQuery)
        });

        assert!(result.is_err());
        let count: i64 = database
            .connection
            .query_row("SELECT count(*) FROM migration_runs", [], |row| row.get(0))
            .expect("count rows");
        assert_eq!(count, 0);
    }

    #[test]
    fn req_data_004_backup_and_restore_preserve_settings() {
        let directory = TempDirectory::new();
        let source_path = directory.path().join("source.sqlite");
        let backup_path = directory.path().join("backup.sqlite");
        let restored_path = directory.path().join("restored.sqlite");
        let source = Database::open(&source_path).expect("open source");
        source
            .save_settings(&sample_settings())
            .expect("save source settings");
        source.backup_to_new(&backup_path).expect("create backup");

        Database::restore_backup_to_new(&backup_path, &restored_path)
            .expect("restore backup to new path");
        let restored = Database::open(&restored_path).expect("open restored database");
        assert_eq!(
            restored.load_settings().expect("load restored settings"),
            Some(sample_settings())
        );
        restored
            .integrity_check()
            .expect("restored database integrity");
    }

    #[test]
    fn req_data_004_restore_replaces_data_and_keeps_a_safety_copy() {
        let directory = TempDirectory::new();
        let backup = directory.path().join("backup.sqlite");
        let safety = directory.path().join("vorher.sqlite");
        let mut database =
            Database::open(directory.path().join("live.sqlite")).expect("open database");
        database
            .save_settings(&sample_settings())
            .expect("save settings");
        database.backup_to_new(&backup).expect("create backup");

        let mut changed = sample_settings();
        changed.first_name = "Geändert".into();
        database.save_settings(&changed).expect("change settings");
        database
            .restore_from_backup(&backup, &safety)
            .expect("restore backup");

        assert_eq!(
            database.load_settings().expect("load settings"),
            Some(sample_settings())
        );
        let previous = Database::open(&safety).expect("open safety copy");
        assert_eq!(
            previous.load_settings().expect("load safety settings"),
            Some(changed)
        );
        database.integrity_check().expect("integrity after restore");
    }

    #[test]
    fn req_data_004_invalid_restore_leaves_data_and_creates_no_safety_copy() {
        let directory = TempDirectory::new();
        let live = directory.path().join("live.sqlite");
        let mut database = Database::open(&live).expect("open database");
        database
            .save_settings(&sample_settings())
            .expect("save settings");
        let safety = directory.path().join("vorher.sqlite");

        let garbage = directory.path().join("kaputt.sqlite");
        fs::write(&garbage, b"keine Datenbank").expect("write garbage");
        assert!(database.restore_from_backup(&garbage, &safety).is_err());
        assert!(!safety.exists());

        let newer = directory.path().join("neuer.sqlite");
        {
            let connection = Connection::open(&newer).expect("open newer");
            connection
                .pragma_update(None, "user_version", CURRENT_SCHEMA_VERSION + 1)
                .expect("set newer version");
        }
        assert!(matches!(
            database.restore_from_backup(&newer, &safety),
            Err(StorageError::UnsupportedSchema { .. })
        ));
        assert!(!safety.exists());

        assert!(matches!(
            database.restore_from_backup(&live, &safety),
            Err(StorageError::InvalidBackup(_))
        ));
        assert!(!safety.exists());

        let backup = directory.path().join("backup.sqlite");
        database.backup_to_new(&backup).expect("create backup");
        fs::write(&safety, b"belegt").expect("occupy safety path");
        assert!(matches!(
            database.restore_from_backup(&backup, &safety),
            Err(StorageError::DestinationExists(_))
        ));
        assert_eq!(fs::read(&safety).expect("read safety"), b"belegt");
        assert_eq!(
            database.load_settings().expect("load settings"),
            Some(sample_settings())
        );
    }

    #[test]
    fn req_data_003_restoring_an_older_schema_upgrades_it() {
        let directory = TempDirectory::new();
        let old = directory.path().join("alt.sqlite");
        {
            let connection = Connection::open(&old).expect("open old");
            connection.execute_batch(MIGRATION_1).expect("schema 1");
            connection
                .pragma_update(None, "user_version", 1)
                .expect("set version 1");
        }
        let mut database = Database::open_in_memory().expect("open database");
        database
            .restore_from_backup(&old, directory.path().join("vorher.sqlite"))
            .expect("restore old backup");
        assert_eq!(
            database.schema_version().expect("schema version"),
            CURRENT_SCHEMA_VERSION
        );
        database.integrity_check().expect("integrity");
    }

    #[test]
    fn req_data_004_backup_never_overwrites_an_existing_file() {
        let directory = TempDirectory::new();
        let database = Database::open_in_memory().expect("open database");
        let destination = directory.path().join("existing.backup");
        fs::write(&destination, b"keep this file").expect("write sentinel");

        assert!(
            matches!(database.backup_to_new(&destination), Err(StorageError::DestinationExists(path)) if path == destination)
        );
        assert_eq!(
            fs::read(destination).expect("read sentinel"),
            b"keep this file"
        );
    }

    #[test]
    fn req_data_004_corrupted_backup_is_rejected_without_creating_restore_target() {
        let directory = TempDirectory::new();
        let backup = directory.path().join("bad.sqlite");
        let destination = directory.path().join("restore.sqlite");
        fs::write(&backup, b"not a sqlite database").expect("write damaged backup");

        assert!(Database::restore_backup_to_new(&backup, &destination).is_err());
        assert!(!destination.exists());
    }

    #[test]
    fn req_data_004_restore_never_overwrites_an_existing_database() {
        let directory = TempDirectory::new();
        let source = Database::open_in_memory().expect("open source");
        let backup = directory.path().join("backup.sqlite");
        let destination = directory.path().join("existing.sqlite");
        source.backup_to_new(&backup).expect("create backup");
        fs::write(&destination, b"preserve existing file").expect("write sentinel");

        assert!(matches!(
            Database::restore_backup_to_new(&backup, &destination),
            Err(StorageError::DestinationExists(path)) if path == destination
        ));
        assert_eq!(
            fs::read(destination).expect("read sentinel"),
            b"preserve existing file"
        );
    }

    #[test]
    fn req_mig_005_import_batch_commits_related_records_and_is_idempotent() {
        let mut database = Database::open_in_memory().expect("open database");
        let batch = sample_import_batch();

        let report = database
            .apply_legacy_import(&batch)
            .expect("apply migration batch");
        assert!(report.settings_imported);
        assert_eq!(report.requests_imported, 1);
        assert_eq!(report.status_events_imported, 2);
        assert_eq!(report.exports_imported, 1);
        assert_eq!(
            database.load_settings().expect("load imported settings"),
            batch.settings
        );

        let aza_count: i64 = database
            .connection
            .query_row("SELECT count(*) FROM request_aza_dates", [], |row| {
                row.get(0)
            })
            .expect("count AZA dates");
        assert_eq!(aza_count, 1);

        let repeated = database
            .apply_legacy_import(&batch)
            .expect("repeated migration is a no-op");
        assert!(repeated.already_imported);
        let request_count: i64 = database
            .connection
            .query_row("SELECT count(*) FROM vacation_requests", [], |row| {
                row.get(0)
            })
            .expect("count requests");
        assert_eq!(request_count, 1);
    }

    #[test]
    fn req_mig_005_conflicting_request_id_rolls_back_settings_and_all_rows() {
        let mut database = Database::open_in_memory().expect("open database");
        let mut batch = sample_import_batch();
        batch.requests.push(batch.requests[0].clone());

        assert!(matches!(
            database.apply_legacy_import(&batch),
            Err(StorageError::MigrationConflict(_))
        ));
        assert_eq!(
            database.load_settings().expect("settings rolled back"),
            None
        );
        for table in [
            "vacation_requests",
            "request_status_events",
            "export_records",
            "migration_runs",
        ] {
            let count: i64 = database
                .connection
                .query_row(&format!("SELECT count(*) FROM {table}"), [], |row| {
                    row.get(0)
                })
                .expect("count table rows");
            assert_eq!(count, 0, "{table} should roll back with batch");
        }
    }

    #[test]
    fn req_mig_005_same_request_from_second_source_is_rejected_without_duplicates() {
        let mut database = Database::open_in_memory().expect("open database");
        let first = sample_import_batch();
        database
            .apply_legacy_import(&first)
            .expect("import first source");

        let mut second = sample_import_batch();
        second.migration_id = format!("history_jsonl:{}", "b".repeat(64));
        second.source_sha256 = "b".repeat(64);
        second.source_format = "history_jsonl".into();
        second.settings = None;
        assert!(matches!(
            database.apply_legacy_import(&second),
            Err(StorageError::MigrationConflict(_))
        ));
        let requests: i64 = database
            .connection
            .query_row("SELECT count(*) FROM vacation_requests", [], |row| {
                row.get(0)
            })
            .expect("count requests");
        assert_eq!(requests, 1);
    }
}
