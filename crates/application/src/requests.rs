use std::path::{Path, PathBuf};

use chrono::{DateTime, Datelike, NaiveDate, Utc};
use urlaubstool_domain::RequestStatus;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DraftRequest {
    pub request_id: String,
    pub start_date: NaiveDate,
    pub end_date: NaiveDate,
    pub start_half_day: bool,
    pub end_half_day: bool,
    pub requested_half_units: u32,
    pub created_at_utc: DateTime<Utc>,
    pub aza_dates: Vec<NaiveDate>,
}

/// Gespeicherter Antrag mit aktuellem Status (REQ-HIST-001).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StoredRequest {
    pub request_id: String,
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
    /// Zählt gegen den Jahresanspruch: genehmigt, oder nach einer Genehmigung
    /// archiviert (Entscheidung E4a). Gelöschte und abgelehnte Anträge zählen nicht.
    pub counts_as_approved: bool,
}

/// Eintrag des Statusverlaufs (REQ-HIST-002).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StatusEvent {
    pub previous_status: Option<RequestStatus>,
    pub new_status: RequestStatus,
    pub occurred_at_utc: DateTime<Utc>,
    pub rejection_reason: Option<String>,
}

/// Nachweis einer geschriebenen PDF-Datei (REQ-HIST-001).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ExportRecord {
    pub file_path: PathBuf,
    pub content_sha256: Option<String>,
    pub exported_at_utc: DateTime<Utc>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ApprovalExceedsEntitlement {
    pub year: i32,
    pub annual_entitlement_half_units: u32,
    pub already_approved_half_units: u64,
    pub requested_half_units: u32,
}

impl std::fmt::Display for ApprovalExceedsEntitlement {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            formatter,
            "Die Genehmigung überschreitet den Jahresurlaub {}: bereits genehmigt {}, beantragt {}, Anspruch {} Tage.",
            self.year,
            format_half_units(self.already_approved_half_units),
            format_half_units(u64::from(self.requested_half_units)),
            format_half_units(u64::from(self.annual_entitlement_half_units)),
        )
    }
}

impl std::error::Error for ApprovalExceedsEntitlement {}

fn format_half_units(units: u64) -> String {
    if units.is_multiple_of(2) {
        format!("{}", units / 2)
    } else {
        format!("{},5", units / 2)
    }
}

/// Summe der genehmigten halben Tage eines Jahres, optional ohne einen Antrag.
pub fn approved_half_units(requests: &[StoredRequest], year: i32, excluding: Option<&str>) -> u64 {
    requests
        .iter()
        .filter(|request| {
            request.counts_as_approved
                && request.start_date.year() == year
                && excluding != Some(request.request_id.as_str())
        })
        .map(|request| u64::from(request.requested_half_units))
        .sum()
}

/// REQ-CALC-008: Eine Genehmigung darf den Jahresanspruch nicht überschreiten.
pub fn ensure_approval_within_entitlement(
    requests: &[StoredRequest],
    candidate: &StoredRequest,
    annual_entitlement_half_units: u32,
) -> Result<(), ApprovalExceedsEntitlement> {
    let year = candidate.start_date.year();
    let already = approved_half_units(requests, year, Some(&candidate.request_id));
    if already + u64::from(candidate.requested_half_units)
        > u64::from(annual_entitlement_half_units)
    {
        return Err(ApprovalExceedsEntitlement {
            year,
            annual_entitlement_half_units,
            already_approved_half_units: already,
            requested_half_units: candidate.requested_half_units,
        });
    }
    Ok(())
}

pub trait RequestRepository {
    type Error;

    fn create_draft(&mut self, request: &DraftRequest) -> Result<(), Self::Error>;
    fn load_drafts(&self) -> Result<Vec<DraftRequest>, Self::Error>;

    /// Setzt einen geprüften Statuswechsel und schreibt ihn in den Statusverlauf.
    /// Unzulässige Wechsel verändern keine Daten.
    fn change_status(
        &mut self,
        request_id: &str,
        new_status: RequestStatus,
        occurred_at_utc: DateTime<Utc>,
        rejection_reason: Option<&str>,
    ) -> Result<(), Self::Error>;

    /// Alle Anträge, neueste Zeiträume zuerst. Gelöschte nur auf Wunsch.
    fn load_requests(&self, include_deleted: bool) -> Result<Vec<StoredRequest>, Self::Error>;

    /// Statusverlauf eines Antrags in der Reihenfolge der Einträge.
    fn load_status_events(&self, request_id: &str) -> Result<Vec<StatusEvent>, Self::Error>;

    /// Hält eine geschriebene Exportdatei fest. Ein Entwurf wird dabei in
    /// derselben Transaktion auf „Exportiert“ gesetzt; andere Status bleiben.
    fn record_export(
        &mut self,
        request_id: &str,
        file_path: &Path,
        content_sha256: Option<&str>,
        exported_at_utc: DateTime<Utc>,
    ) -> Result<(), Self::Error>;

    /// Exportnachweise eines Antrags, ältester zuerst.
    fn load_exports(&self, request_id: &str) -> Result<Vec<ExportRecord>, Self::Error>;
}

#[cfg(test)]
mod tests {
    use super::*;

    fn request(id: &str, year: i32, units: u32, status: RequestStatus) -> StoredRequest {
        let date = NaiveDate::from_ymd_opt(year, 3, 16).unwrap();
        let time = DateTime::parse_from_rfc3339("2026-01-01T00:00:00Z")
            .unwrap()
            .with_timezone(&Utc);
        StoredRequest {
            request_id: id.into(),
            start_date: date,
            end_date: date,
            start_half_day: false,
            end_half_day: false,
            requested_half_units: units,
            status,
            rejection_reason: None,
            archived_at_utc: None,
            created_at_utc: time,
            updated_at_utc: time,
            aza_dates: vec![],
            counts_as_approved: status == RequestStatus::Approved,
        }
    }

    #[test]
    fn req_calc_008_archived_requests_keep_counting_when_they_were_approved() {
        let mut archived_approved = request("a", 2026, 50, RequestStatus::Archived);
        archived_approved.counts_as_approved = true;
        let archived_rejected = request("b", 2026, 40, RequestStatus::Archived);
        let stored = vec![archived_approved, archived_rejected];
        assert_eq!(approved_half_units(&stored, 2026, None), 50);
        let next = request("c", 2026, 11, RequestStatus::Exported);
        assert!(ensure_approval_within_entitlement(&stored, &next, 60).is_err());
        let fitting = request("d", 2026, 10, RequestStatus::Exported);
        assert_eq!(
            ensure_approval_within_entitlement(&stored, &fitting, 60),
            Ok(())
        );
    }

    #[test]
    fn req_calc_008_approval_may_use_the_entitlement_exactly_but_not_exceed_it() {
        let stored = vec![
            request("a", 2026, 50, RequestStatus::Approved),
            request("b", 2026, 9, RequestStatus::Exported),
            request("c", 2025, 60, RequestStatus::Approved),
            request("d", 2026, 40, RequestStatus::Rejected),
        ];
        let exact = request("e", 2026, 10, RequestStatus::Exported);
        assert_eq!(
            ensure_approval_within_entitlement(&stored, &exact, 60),
            Ok(())
        );
        let over = request("f", 2026, 11, RequestStatus::Exported);
        let error = ensure_approval_within_entitlement(&stored, &over, 60).unwrap_err();
        assert_eq!(error.already_approved_half_units, 50);
        assert!(error.to_string().contains("5,5"));
    }

    #[test]
    fn req_calc_008_a_request_is_not_counted_against_itself() {
        let approved = request("a", 2026, 60, RequestStatus::Approved);
        let stored = vec![approved.clone()];
        assert_eq!(approved_half_units(&stored, 2026, Some("a")), 0);
        assert_eq!(approved_half_units(&stored, 2026, None), 60);
        assert_eq!(
            ensure_approval_within_entitlement(&stored, &approved, 60),
            Ok(())
        );
    }
}
