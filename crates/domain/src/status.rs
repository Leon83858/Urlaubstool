//! Statusmodell eines Urlaubsantrags (REQ-HIST-003, REQ-HIST-005).
//!
//! Übergangstabelle nach Entscheidung E4 des Auftraggebers vom 10.11.2026:
//! Archivieren und Löschen sind nur nach einer Ablehnung möglich; ein genehmigter
//! Antrag kann nachträglich mit Grund abgelehnt werden. Entwürfe und exportierte
//! Anträge bleiben direkt löschbar. Genehmigt werden kann nur ein exportierter
//! Antrag, weil sich die Genehmigung auf das eingereichte Formular bezieht.
//! Löschen ist ein wiederherstellbares Soft-Delete.
//!
//! Ergänzung vom 12.11.2026 (Entscheidung des Auftraggebers): Ein archivierter
//! Antrag kann „ent-archiviert“ werden und kehrt dann in den Status vor dem
//! Archivieren zurück (abgelehnt bzw. bei übernommenen Altfällen genehmigt).

use std::fmt;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum RequestStatus {
    Draft,
    Exported,
    Approved,
    Rejected,
    Archived,
    Deleted,
}

impl RequestStatus {
    pub const ALL: [Self; 6] = [
        Self::Draft,
        Self::Exported,
        Self::Approved,
        Self::Rejected,
        Self::Archived,
        Self::Deleted,
    ];

    /// Stabiler Speicherwert; identisch mit den Statusnamen der Altanwendung.
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Draft => "Draft",
            Self::Exported => "Exported",
            Self::Approved => "Approved",
            Self::Rejected => "Rejected",
            Self::Archived => "Archived",
            Self::Deleted => "Deleted",
        }
    }

    pub fn parse(value: &str) -> Option<Self> {
        Self::ALL
            .into_iter()
            .find(|status| status.as_str() == value)
    }

    /// Deutsche Bezeichnung für Oberfläche und Meldungen.
    pub const fn label(self) -> &'static str {
        match self {
            Self::Draft => "Entwurf",
            Self::Exported => "Exportiert",
            Self::Approved => "Genehmigt",
            Self::Rejected => "Abgelehnt",
            Self::Archived => "Archiviert",
            Self::Deleted => "Gelöscht",
        }
    }

    /// Reguläre Übergänge. Das Wiederherstellen eines gelöschten und das
    /// Ent-Archivieren eines archivierten Antrags hängen vom vorherigen Status ab
    /// und werden in [`validate_transition`] geprüft.
    pub const fn can_transition_to(self, next: Self) -> bool {
        matches!(
            (self, next),
            (Self::Draft, Self::Exported | Self::Deleted)
                | (
                    Self::Exported,
                    Self::Approved | Self::Rejected | Self::Deleted
                )
                | (Self::Approved, Self::Rejected)
                | (Self::Rejected, Self::Archived | Self::Deleted)
                | (Self::Archived, Self::Deleted)
        )
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StatusTransitionError {
    NotAllowed {
        from: RequestStatus,
        to: RequestStatus,
    },
    RejectionReasonRequired,
    RejectionReasonNotAllowed {
        to: RequestStatus,
    },
    RestoreTargetMismatch {
        expected: RequestStatus,
        requested: RequestStatus,
    },
    RestoreTargetUnknown,
    UnarchiveTargetMismatch {
        expected: RequestStatus,
        requested: RequestStatus,
    },
    UnarchiveTargetUnknown,
}

impl fmt::Display for StatusTransitionError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::NotAllowed { from, to } => write!(
                formatter,
                "Ein Antrag mit Status „{}“ kann nicht auf „{}“ gesetzt werden.",
                from.label(),
                to.label()
            ),
            Self::RejectionReasonRequired => {
                formatter.write_str("Für eine Ablehnung muss ein Grund angegeben werden.")
            }
            Self::RejectionReasonNotAllowed { to } => write!(
                formatter,
                "Ein Ablehnungsgrund ist beim Wechsel auf „{}“ nicht zulässig.",
                to.label()
            ),
            Self::RestoreTargetMismatch {
                expected,
                requested,
            } => write!(
                formatter,
                "Ein gelöschter Antrag kann nur mit seinem früheren Status „{}“ wiederhergestellt werden, nicht als „{}“.",
                expected.label(),
                requested.label()
            ),
            Self::RestoreTargetUnknown => formatter.write_str(
                "Der Status vor dem Löschen ist nicht bekannt; der Antrag kann nicht wiederhergestellt werden.",
            ),
            Self::UnarchiveTargetMismatch {
                expected,
                requested,
            } => write!(
                formatter,
                "Ein archivierter Antrag kann nur in seinen früheren Status „{}“ zurückgesetzt werden, nicht in „{}“.",
                expected.label(),
                requested.label()
            ),
            Self::UnarchiveTargetUnknown => formatter.write_str(
                "Der Status vor dem Archivieren ist nicht bekannt; der Antrag kann nicht ent-archiviert werden.",
            ),
        }
    }
}

impl std::error::Error for StatusTransitionError {}

/// Prüft einen Statuswechsel einschließlich Ablehnungsgrund, Wiederherstellung
/// und Ent-Archivieren.
///
/// `previous_status` ist der Status vor dem Löschen (wenn `current` gelöscht ist)
/// bzw. vor dem Archivieren (wenn `current` archiviert ist); sonst wird er nicht
/// ausgewertet.
pub fn validate_transition(
    current: RequestStatus,
    previous_status: Option<RequestStatus>,
    next: RequestStatus,
    rejection_reason: Option<&str>,
) -> Result<(), StatusTransitionError> {
    let has_reason = rejection_reason.is_some_and(|reason| !reason.trim().is_empty());
    let status_before_deletion = previous_status;
    if current == RequestStatus::Archived && next != RequestStatus::Deleted {
        let expected = previous_status.ok_or(StatusTransitionError::UnarchiveTargetUnknown)?;
        if !matches!(expected, RequestStatus::Rejected | RequestStatus::Approved) {
            return Err(StatusTransitionError::UnarchiveTargetUnknown);
        }
        if expected != next {
            return Err(StatusTransitionError::UnarchiveTargetMismatch {
                expected,
                requested: next,
            });
        }
        // Der bisherige Ablehnungsgrund bleibt erhalten; ein neuer ist nicht zulässig.
        if has_reason {
            return Err(StatusTransitionError::RejectionReasonNotAllowed { to: next });
        }
        return Ok(());
    }
    if current == RequestStatus::Deleted {
        if next == RequestStatus::Deleted {
            return Err(StatusTransitionError::NotAllowed {
                from: current,
                to: next,
            });
        }
        let expected = status_before_deletion.ok_or(StatusTransitionError::RestoreTargetUnknown)?;
        if expected == RequestStatus::Deleted {
            return Err(StatusTransitionError::RestoreTargetUnknown);
        }
        if expected != next {
            return Err(StatusTransitionError::RestoreTargetMismatch {
                expected,
                requested: next,
            });
        }
        if has_reason {
            return Err(StatusTransitionError::RejectionReasonNotAllowed { to: next });
        }
        return Ok(());
    }
    if !current.can_transition_to(next) {
        return Err(StatusTransitionError::NotAllowed {
            from: current,
            to: next,
        });
    }
    match (next, has_reason) {
        (RequestStatus::Rejected, false) => Err(StatusTransitionError::RejectionReasonRequired),
        (RequestStatus::Rejected, true) => Ok(()),
        (_, true) => Err(StatusTransitionError::RejectionReasonNotAllowed { to: next }),
        (_, false) => Ok(()),
    }
}

#[cfg(test)]
mod tests {
    use super::RequestStatus::{Approved, Archived, Deleted, Draft, Exported, Rejected};
    use super::*;

    #[test]
    fn req_hist_003_transition_table_is_exhaustive() {
        let allowed = [
            (Draft, Exported),
            (Draft, Deleted),
            (Exported, Approved),
            (Exported, Rejected),
            (Exported, Deleted),
            (Approved, Rejected),
            (Rejected, Archived),
            (Rejected, Deleted),
            (Archived, Deleted),
        ];
        for from in RequestStatus::ALL {
            for to in RequestStatus::ALL {
                assert_eq!(
                    from.can_transition_to(to),
                    allowed.contains(&(from, to)),
                    "{from:?} -> {to:?}"
                );
            }
        }
    }

    #[test]
    fn req_hist_003_rejection_requires_a_reason_and_only_rejection_takes_one() {
        assert_eq!(
            validate_transition(Exported, None, Rejected, None),
            Err(StatusTransitionError::RejectionReasonRequired)
        );
        assert_eq!(
            validate_transition(Exported, None, Rejected, Some("  ")),
            Err(StatusTransitionError::RejectionReasonRequired)
        );
        assert_eq!(
            validate_transition(Exported, None, Rejected, Some("Termin belegt")),
            Ok(())
        );
        assert_eq!(
            validate_transition(Exported, None, Approved, Some("Termin belegt")),
            Err(StatusTransitionError::RejectionReasonNotAllowed { to: Approved })
        );
        assert_eq!(
            validate_transition(Exported, None, Approved, Some(" ")),
            Ok(())
        );
    }

    #[test]
    fn req_hist_003_approved_requests_can_only_be_rejected_with_a_reason() {
        for next in RequestStatus::ALL {
            let result = validate_transition(Approved, None, next, Some("Urlaub storniert"));
            assert_eq!(result.is_ok(), next == Rejected, "Approved -> {next:?}");
        }
        assert_eq!(
            validate_transition(Approved, None, Rejected, None),
            Err(StatusTransitionError::RejectionReasonRequired)
        );
        assert_eq!(
            validate_transition(Approved, None, Archived, None),
            Err(StatusTransitionError::NotAllowed {
                from: Approved,
                to: Archived
            })
        );
    }

    #[test]
    fn req_hist_005_archived_requests_can_only_be_deleted() {
        for next in RequestStatus::ALL {
            let result = validate_transition(Archived, None, next, None);
            assert_eq!(result.is_ok(), next == Deleted, "Archived -> {next:?}");
        }
    }

    #[test]
    fn req_hist_005_deleted_requests_restore_only_to_their_previous_status() {
        for previous in [Draft, Exported, Approved, Rejected, Archived] {
            for next in RequestStatus::ALL {
                let result = validate_transition(Deleted, Some(previous), next, None);
                assert_eq!(result.is_ok(), next == previous, "{previous:?} -> {next:?}");
            }
        }
        assert_eq!(
            validate_transition(Deleted, None, Draft, None),
            Err(StatusTransitionError::RestoreTargetUnknown)
        );
        assert_eq!(
            validate_transition(Deleted, Some(Deleted), Draft, None),
            Err(StatusTransitionError::RestoreTargetUnknown)
        );
        assert_eq!(
            validate_transition(Deleted, Some(Rejected), Rejected, Some("neu")),
            Err(StatusTransitionError::RejectionReasonNotAllowed { to: Rejected })
        );
    }

    #[test]
    fn req_hist_003_storage_values_round_trip_and_unknown_values_are_rejected() {
        for status in RequestStatus::ALL {
            assert_eq!(RequestStatus::parse(status.as_str()), Some(status));
        }
        assert_eq!(RequestStatus::parse("draft"), None);
        assert_eq!(RequestStatus::parse(""), None);
    }

    #[test]
    fn req_diag_002_transition_errors_are_understandable_german_messages() {
        // Regel seit 12.11.2026: Ent-Archivieren nur in den früheren Status.
        let message = validate_transition(Draft, None, Approved, None)
            .unwrap_err()
            .to_string();
        assert!(message.contains("Entwurf") && message.contains("Genehmigt"));
        let message = validate_transition(Archived, Some(Rejected), Approved, None)
            .unwrap_err()
            .to_string();
        assert!(
            message.contains("archivierter")
                && message.contains("Abgelehnt")
                && message.contains("Genehmigt")
        );
    }

    #[test]
    fn req_hist_005_archived_requests_return_only_to_their_previous_status() {
        assert_eq!(
            validate_transition(Archived, Some(Rejected), Rejected, None),
            Ok(())
        );
        assert_eq!(
            validate_transition(Archived, Some(Approved), Approved, None),
            Ok(())
        );
        assert_eq!(
            validate_transition(Archived, Some(Rejected), Approved, None),
            Err(StatusTransitionError::UnarchiveTargetMismatch {
                expected: Rejected,
                requested: Approved
            })
        );
        assert_eq!(
            validate_transition(Archived, None, Rejected, None),
            Err(StatusTransitionError::UnarchiveTargetUnknown)
        );
        assert_eq!(
            validate_transition(Archived, Some(Exported), Exported, None),
            Err(StatusTransitionError::UnarchiveTargetUnknown)
        );
        // Kein neuer Ablehnungsgrund beim Ent-Archivieren.
        assert_eq!(
            validate_transition(Archived, Some(Rejected), Rejected, Some("neu")),
            Err(StatusTransitionError::RejectionReasonNotAllowed { to: Rejected })
        );
        // Löschen bleibt wie bisher möglich, Entwurf/Exportiert sind kein Ziel.
        assert_eq!(
            validate_transition(Archived, Some(Rejected), Deleted, None),
            Ok(())
        );
        for next in [Draft, Exported, Archived] {
            assert!(
                validate_transition(Archived, Some(Rejected), next, None).is_err(),
                "{next:?}"
            );
        }
    }
}
