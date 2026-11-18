#![forbid(unsafe_code)]

mod calculation;
mod calendar;
mod migration;
mod pdf;
mod requests;
mod settings;

pub use calculation::{
    calculate_vacation, calendar_year, CalendarDayOutput, StudentCalendar,
    VacationCalculationInput, VacationCalculationOutput, VacationDayOutput,
};
pub use calendar::{SchoolBreakRecord, SchoolHolidayImport};
pub use migration::{
    ImportCommitReport, LegacyImportBatch, MigratedExportRecord, MigratedRequest,
    MigratedStatusEvent, MigrationRepository, RequestStatus,
};
pub use pdf::{
    prepare_request_pdf, render_request_pdf, PdfDay, PdfError, PdfPersonalDetails, PdfRequestData,
};
pub use requests::{
    approved_half_units, ensure_approval_within_entitlement, ApprovalExceedsEntitlement,
    DraftRequest, ExportRecord, RequestRepository, StatusEvent, StoredRequest,
};
pub use settings::{SettingsRepository, UserSettings};
pub use urlaubstool_domain::{
    validate_transition as validate_status_transition, StatusTransitionError,
};
