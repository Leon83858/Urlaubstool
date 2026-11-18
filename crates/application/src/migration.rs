use std::path::PathBuf;

use chrono::{DateTime, NaiveDate, Utc};

pub use urlaubstool_domain::RequestStatus;

use crate::settings::UserSettings;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MigratedRequest {
    pub request_id: String,
    pub year: i32,
    pub start_date: NaiveDate,
    pub end_date: NaiveDate,
    pub start_half_day: bool,
    pub end_half_day: bool,
    pub requested_half_units: u32,
    pub status: RequestStatus,
    pub rejection_reason: Option<String>,
    pub archived_at_utc: Option<DateTime<Utc>>,
    pub created_at_utc: DateTime<Utc>,
    pub updated_at_utc: DateTime<Utc>,
    pub aza_dates: Vec<NaiveDate>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MigratedStatusEvent {
    pub event_id: String,
    pub request_id: String,
    pub previous_status: Option<RequestStatus>,
    pub new_status: RequestStatus,
    pub occurred_at_utc: DateTime<Utc>,
    pub rejection_reason: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MigratedExportRecord {
    pub export_id: String,
    pub request_id: String,
    pub file_path: PathBuf,
    pub content_sha256: Option<String>,
    pub exported_at_utc: DateTime<Utc>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LegacyImportBatch {
    pub migration_id: String,
    pub source_path: PathBuf,
    pub source_sha256: String,
    pub source_format: String,
    pub started_at_utc: DateTime<Utc>,
    pub committed_at_utc: DateTime<Utc>,
    pub records_read: usize,
    pub records_ready: usize,
    pub records_skipped: usize,
    pub records_failed: usize,
    pub skipped_records_confirmed: bool,
    pub settings: Option<UserSettings>,
    pub requests: Vec<MigratedRequest>,
    pub status_events: Vec<MigratedStatusEvent>,
    pub exports: Vec<MigratedExportRecord>,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct ImportCommitReport {
    pub already_imported: bool,
    pub settings_imported: bool,
    pub requests_imported: u64,
    pub status_events_imported: u64,
    pub exports_imported: u64,
}

pub trait MigrationRepository {
    type Error: std::fmt::Display;

    fn apply_legacy_import(
        &mut self,
        batch: &LegacyImportBatch,
    ) -> Result<ImportCommitReport, Self::Error>;
}
