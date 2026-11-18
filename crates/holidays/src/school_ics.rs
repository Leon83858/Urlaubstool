use std::io::{BufReader, Cursor};

use chrono::{DateTime, NaiveDate, NaiveDateTime, Utc};
use ical::{parser::ical::component::IcalEvent, IcalParser};

use crate::GermanState;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SchoolBreak {
    pub start_date: NaiveDate,
    pub end_date_exclusive: NaiveDate,
    pub summary: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SchoolHolidayCalendar {
    pub state: GermanState,
    pub source_url: String,
    pub fetched_at_utc: DateTime<Utc>,
    pub latest_event_stamp_utc: Option<DateTime<Utc>>,
    breaks: Vec<SchoolBreak>,
}

impl SchoolHolidayCalendar {
    pub fn breaks(&self) -> &[SchoolBreak] {
        &self.breaks
    }

    pub fn is_school_vacation(&self, date: NaiveDate) -> bool {
        self.breaks
            .iter()
            .any(|break_| date >= break_.start_date && date < break_.end_date_exclusive)
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SchoolHolidayImportError {
    NonKmkSource,
    InvalidIcal(String),
    NoEvents,
    MissingProperty {
        event: usize,
        property: &'static str,
    },
    MissingSummary {
        event: usize,
    },
    DuplicateProperty {
        event: usize,
        property: &'static str,
    },
    NonDateProperty {
        event: usize,
        property: &'static str,
    },
    InvalidDate {
        event: usize,
        property: &'static str,
    },
    InvalidRange {
        event: usize,
    },
    StateMismatch {
        event: usize,
        expected: &'static str,
    },
}

impl SchoolHolidayImportError {
    pub fn message(&self) -> String {
        match self {
            Self::NonKmkSource => "Die Ferienquelle ist keine KMK-Adresse.".into(),
            Self::InvalidIcal(reason) => format!("Der iCalendar-Datensatz ist ungültig: {reason}"),
            Self::NoEvents => "Der Ferienkalender enthält keine Termine.".into(),
            Self::MissingProperty { event, property } => {
                format!("Termin {event} enthält kein Feld {property}.")
            }
            Self::MissingSummary { event } => format!("Termin {event} hat keine Bezeichnung."),
            Self::DuplicateProperty { event, property } => {
                format!("Termin {event} enthält {property} mehrfach.")
            }
            Self::NonDateProperty { event, property } => {
                format!("Termin {event} enthält keinen reinen DATE-Wert in {property}.")
            }
            Self::InvalidDate { event, property } => {
                format!("Termin {event} enthält kein gültiges Ganztagesdatum in {property}.")
            }
            Self::InvalidRange { event } => {
                format!("Termin {event} hat ein Enddatum vor oder gleich dem Startdatum.")
            }
            Self::StateMismatch { event, expected } => {
                format!("Termin {event} gehört nicht zum gewählten Bundesland {expected}.")
            }
        }
    }
}

pub fn parse_kmk_ics(
    state: GermanState,
    source_url: impl Into<String>,
    fetched_at_utc: DateTime<Utc>,
    contents: &str,
) -> Result<SchoolHolidayCalendar, Vec<SchoolHolidayImportError>> {
    let source_url = source_url.into();
    if !source_url.starts_with("https://www.kmk.org/") {
        return Err(vec![SchoolHolidayImportError::NonKmkSource]);
    }

    let reader = BufReader::new(Cursor::new(contents.as_bytes()));
    let mut parser = IcalParser::new(reader);
    let mut raw_events = Vec::new();
    let mut errors = Vec::new();

    for calendar in &mut parser {
        match calendar {
            Ok(calendar) => raw_events.extend(calendar.events),
            Err(error) => {
                errors.push(SchoolHolidayImportError::InvalidIcal(format!("{error:?}")));
                break;
            }
        }
    }

    if !errors.is_empty() {
        return Err(errors);
    }
    if raw_events.is_empty() {
        return Err(vec![SchoolHolidayImportError::NoEvents]);
    }

    let mut breaks = Vec::new();
    let mut latest_event_stamp_utc = None;
    for (index, event) in raw_events.iter().enumerate() {
        match parse_event(event, index + 1, state) {
            Ok((break_, updated_at)) => {
                if let Some(updated_at) = updated_at {
                    latest_event_stamp_utc = Some(
                        latest_event_stamp_utc
                            .map_or(updated_at, |current: DateTime<Utc>| current.max(updated_at)),
                    );
                }
                breaks.push(break_);
            }
            Err(error) => errors.push(error),
        }
    }

    if !errors.is_empty() {
        return Err(errors);
    }

    breaks.sort_by_key(|break_| (break_.start_date, break_.end_date_exclusive));
    Ok(SchoolHolidayCalendar {
        state,
        source_url,
        fetched_at_utc,
        latest_event_stamp_utc,
        breaks,
    })
}

fn parse_event(
    event: &IcalEvent,
    event_index: usize,
    state: GermanState,
) -> Result<(SchoolBreak, Option<DateTime<Utc>>), SchoolHolidayImportError> {
    let start_date = date_property(event, event_index, "DTSTART")?;
    let end_date_exclusive = date_property(event, event_index, "DTEND")?;
    let summary = required_property(event, event_index, "SUMMARY")?
        .value
        .as_deref()
        .filter(|value| !value.trim().is_empty())
        .ok_or(SchoolHolidayImportError::MissingSummary { event: event_index })?;

    if end_date_exclusive <= start_date {
        return Err(SchoolHolidayImportError::InvalidRange { event: event_index });
    }
    if !summary.contains(state.name()) {
        return Err(SchoolHolidayImportError::StateMismatch {
            event: event_index,
            expected: state.name(),
        });
    }

    let updated_at = event
        .properties
        .iter()
        .find(|property| property.name == "DTSTAMP")
        .and_then(|property| property.value.as_deref())
        .and_then(parse_utc_stamp);

    Ok((
        SchoolBreak {
            start_date,
            end_date_exclusive,
            summary: summary.to_owned(),
        },
        updated_at,
    ))
}

fn required_property<'a>(
    event: &'a IcalEvent,
    event_index: usize,
    name: &'static str,
) -> Result<&'a ical::property::Property, SchoolHolidayImportError> {
    let mut properties = event
        .properties
        .iter()
        .filter(|property| property.name == name);
    let property = properties
        .next()
        .ok_or(SchoolHolidayImportError::MissingProperty {
            event: event_index,
            property: name,
        })?;
    if properties.next().is_some() {
        return Err(SchoolHolidayImportError::DuplicateProperty {
            event: event_index,
            property: name,
        });
    }
    Ok(property)
}

fn date_property(
    event: &IcalEvent,
    event_index: usize,
    name: &'static str,
) -> Result<NaiveDate, SchoolHolidayImportError> {
    let property = required_property(event, event_index, name)?;
    let has_date_value = property.params.as_ref().is_some_and(|params| {
        params
            .iter()
            .any(|(key, values)| key == "VALUE" && values.iter().any(|value| value == "DATE"))
    });
    if !has_date_value {
        return Err(SchoolHolidayImportError::NonDateProperty {
            event: event_index,
            property: name,
        });
    }
    let value = property
        .value
        .as_deref()
        .ok_or(SchoolHolidayImportError::MissingProperty {
            event: event_index,
            property: name,
        })?;
    parse_date(value).ok_or(SchoolHolidayImportError::InvalidDate {
        event: event_index,
        property: name,
    })
}

fn parse_date(value: &str) -> Option<NaiveDate> {
    if value.len() != 8 || !value.bytes().all(|byte| byte.is_ascii_digit()) {
        return None;
    }
    NaiveDate::parse_from_str(value, "%Y%m%d").ok()
}

fn parse_utc_stamp(value: &str) -> Option<DateTime<Utc>> {
    NaiveDateTime::parse_from_str(value, "%Y%m%dT%H%M%SZ")
        .ok()
        .map(|date_time| date_time.and_utc())
}

#[cfg(test)]
mod tests {
    use chrono::DateTime;

    use super::*;

    const KMK_SOURCE: &str = "https://www.kmk.org/service/ferienregelung/ferienkalender";

    fn fetched_at() -> DateTime<Utc> {
        DateTime::parse_from_rfc3339("2026-11-10T12:00:00+00:00")
            .expect("valid test timestamp")
            .to_utc()
    }

    fn event(start: &str, end: &str, summary: &str) -> String {
        format!(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nDTSTAMP:20251008T220000Z\r\nDTSTART;VALUE=DATE:{start}\r\nDTEND;VALUE=DATE:{end}\r\nSUMMARY:{summary}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"
        )
    }

    #[test]
    fn req_hol_003_kmk_ics_keeps_source_fetch_and_event_stamp_metadata() {
        let contents = event(
            "20261223",
            "20270110",
            "Weihnachten - Baden-Württemberg - Ferien im Schuljahr 2026/2027",
        );
        let calendar = parse_kmk_ics(
            GermanState::BadenWuerttemberg,
            KMK_SOURCE,
            fetched_at(),
            &contents,
        )
        .expect("valid KMK feed");

        assert_eq!(calendar.source_url, KMK_SOURCE);
        assert_eq!(calendar.fetched_at_utc, fetched_at());
        assert_eq!(
            calendar.latest_event_stamp_utc,
            Some(
                DateTime::parse_from_rfc3339("2025-10-08T22:00:00+00:00")
                    .expect("valid source timestamp")
                    .to_utc()
            )
        );
    }

    #[test]
    fn req_hol_001_kmk_ics_uses_exclusive_end_date() {
        let contents = event(
            "20261223",
            "20270110",
            "Weihnachten - Baden-Württemberg - Ferien im Schuljahr 2026/2027",
        );
        let calendar = parse_kmk_ics(
            GermanState::BadenWuerttemberg,
            KMK_SOURCE,
            fetched_at(),
            &contents,
        )
        .expect("valid KMK feed");

        assert!(calendar.is_school_vacation(NaiveDate::from_ymd_opt(2026, 12, 23).expect("date")));
        assert!(calendar.is_school_vacation(NaiveDate::from_ymd_opt(2027, 1, 9).expect("date")));
        assert!(!calendar.is_school_vacation(NaiveDate::from_ymd_opt(2027, 1, 10).expect("date")));
    }

    #[test]
    fn req_hol_004_malformed_dates_and_reversed_ranges_are_rejected() {
        let malformed = event(
            "20261340",
            "20270110",
            "Weihnachten - Baden-Württemberg - Ferien im Schuljahr 2026/2027",
        );
        assert!(matches!(
            parse_kmk_ics(GermanState::BadenWuerttemberg, KMK_SOURCE, fetched_at(), &malformed),
            Err(errors) if errors.contains(&SchoolHolidayImportError::InvalidDate { event: 1, property: "DTSTART" })
        ));

        let reversed = event(
            "20270110",
            "20261223",
            "Weihnachten - Baden-Württemberg - Ferien im Schuljahr 2026/2027",
        );
        assert!(matches!(
            parse_kmk_ics(GermanState::BadenWuerttemberg, KMK_SOURCE, fetched_at(), &reversed),
            Err(errors) if errors.contains(&SchoolHolidayImportError::InvalidRange { event: 1 })
        ));
    }

    #[test]
    fn req_hol_004_datetime_events_are_rejected() {
        let contents = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nDTSTART:20261223T090000\r\nDTEND:20261223T170000\r\nSUMMARY:Ferien - Baden-Württemberg\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        assert!(matches!(
            parse_kmk_ics(
                GermanState::BadenWuerttemberg,
                KMK_SOURCE,
                fetched_at(),
                contents,
            ),
            Err(errors) if errors.contains(&SchoolHolidayImportError::NonDateProperty {
                event: 1,
                property: "DTSTART",
            })
        ));
    }

    #[test]
    fn req_hol_003_feed_must_come_from_the_kmk_host() {
        let contents = event(
            "20261223",
            "20270110",
            "Weihnachten - Baden-Württemberg - Ferien im Schuljahr 2026/2027",
        );
        assert_eq!(
            parse_kmk_ics(
                GermanState::BadenWuerttemberg,
                "https://calendar.invalid/holidays.ics",
                fetched_at(),
                &contents,
            ),
            Err(vec![SchoolHolidayImportError::NonKmkSource])
        );
    }
}
