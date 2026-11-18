use std::collections::{BTreeMap, BTreeSet};

use chrono::{Datelike, NaiveDate, Weekday};

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, PartialOrd, Ord)]
pub struct DayUnits(u32);

impl DayUnits {
    pub const ZERO: Self = Self(0);
    pub const HALF_DAY: Self = Self(1);
    pub const ONE_DAY: Self = Self(2);

    pub const fn from_half_units(half_units: u32) -> Self {
        Self(half_units)
    }

    pub const fn half_units(self) -> u32 {
        self.0
    }
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct WorkWeek(u8);

impl WorkWeek {
    pub fn from_weekdays(days: impl IntoIterator<Item = Weekday>) -> Self {
        let mut mask = 0;
        for day in days {
            mask |= 1 << day.num_days_from_monday();
        }
        Self(mask)
    }

    pub fn standard() -> Self {
        Self::from_weekdays([
            Weekday::Mon,
            Weekday::Tue,
            Weekday::Wed,
            Weekday::Thu,
            Weekday::Fri,
        ])
    }

    pub fn contains(self, day: Weekday) -> bool {
        self.0 & (1 << day.num_days_from_monday()) != 0
    }

    pub const fn is_empty(self) -> bool {
        self.0 == 0
    }
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub enum VocationalSchoolType {
    #[default]
    None,
    Half,
    Full,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StudentRules {
    pub active: bool,
    weekdays: [VocationalSchoolType; 7],
}

impl StudentRules {
    pub fn set_weekday(&mut self, day: Weekday, school_type: VocationalSchoolType) {
        self.weekdays[day.num_days_from_monday() as usize] = school_type;
    }

    pub fn school_type(&self, day: Weekday) -> VocationalSchoolType {
        self.weekdays[day.num_days_from_monday() as usize]
    }
}

impl Default for StudentRules {
    fn default() -> Self {
        Self {
            active: false,
            weekdays: [VocationalSchoolType::None; 7],
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PublicHolidayKind {
    Mandatory,
    Optional,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct CalendarDayFacts {
    pub public_holiday: Option<PublicHolidayKind>,
    pub is_school_vacation: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct CalendarFacts {
    days: BTreeMap<NaiveDate, CalendarDayFacts>,
}

impl CalendarFacts {
    pub fn insert(&mut self, date: NaiveDate, facts: CalendarDayFacts) {
        self.days.insert(date, facts);
    }

    fn get(&self, date: NaiveDate) -> Option<CalendarDayFacts> {
        self.days.get(&date).copied()
    }
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct AzaDates(BTreeSet<NaiveDate>);

impl AzaDates {
    pub fn insert(&mut self, date: NaiveDate) -> bool {
        self.0.insert(date)
    }

    pub fn contains(&self, date: NaiveDate) -> bool {
        self.0.contains(&date)
    }

    pub fn iter(&self) -> impl Iterator<Item = &NaiveDate> {
        self.0.iter()
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VacationRequest {
    pub start_date: NaiveDate,
    pub end_date: NaiveDate,
    pub start_half_day: bool,
    pub end_half_day: bool,
    pub work_week: WorkWeek,
    pub student_rules: StudentRules,
    pub include_optional_holidays: bool,
    pub annual_entitlement: DayUnits,
    pub already_approved: DayUnits,
    pub aza_dates: AzaDates,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum DayReason {
    NonWorkday,
    MandatoryHoliday,
    OptionalHolidayIncluded,
    OptionalHolidayNotIncluded,
    Aza,
    SchoolVacation,
    FullDayVocationalSchool,
    HalfDayVocationalSchool,
    StartHalfDay,
    EndHalfDay,
    OrdinaryWorkday,
}

impl DayReason {
    pub fn message(&self) -> &'static str {
        match self {
            Self::NonWorkday => "Kein konfigurierter Arbeitstag.",
            Self::MandatoryHoliday => "Gesetzlicher Feiertag; kein Urlaubsverbrauch.",
            Self::OptionalHolidayIncluded => "Optionaler Feiertag ist aktiviert.",
            Self::OptionalHolidayNotIncluded => "Optionaler Feiertag ist nicht aktiviert.",
            Self::Aza => "AZA-Tag; kein Urlaubsverbrauch.",
            Self::SchoolVacation => "Schulferien; Berufsschule wird nicht angesetzt.",
            Self::FullDayVocationalSchool => "Ganztägige Berufsschule blockiert den Antrag.",
            Self::HalfDayVocationalSchool => {
                "Halbtägige Berufsschule begrenzt den Verbrauch auf 0,5 Tage."
            }
            Self::StartHalfDay => "Halbtag am Startdatum.",
            Self::EndHalfDay => "Halbtag am Enddatum.",
            Self::OrdinaryWorkday => "Konfigurierter Arbeitstag.",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DayEvaluation {
    pub date: NaiveDate,
    pub weekday: Weekday,
    pub is_workday: bool,
    pub public_holiday: Option<PublicHolidayKind>,
    pub is_school_vacation: bool,
    pub vocational_school: VocationalSchoolType,
    pub is_aza: bool,
    pub start_half_day: bool,
    pub end_half_day: bool,
    pub counted: DayUnits,
    pub reasons: Vec<DayReason>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CalculationError {
    StartAfterEnd,
    CrossesCalendarYear,
    EmptyWorkWeek,
    ZeroAnnualEntitlement,
    ApprovedExceedsEntitlement,
    BothHalfDaysOnSingleDay,
    AzaOutsideRequestRange {
        date: NaiveDate,
    },
    FullDayVocationalSchool {
        dates: Vec<NaiveDate>,
    },
    ExceedsRemaining {
        requested: DayUnits,
        remaining_half_units: i64,
    },
    DateRangeOverflow,
}

impl CalculationError {
    pub fn message(&self) -> String {
        match self {
            Self::StartAfterEnd => "Das Startdatum darf nicht nach dem Enddatum liegen.".into(),
            Self::CrossesCalendarYear => {
                "Zeitraum überschreitet das Kalenderjahr. Bitte zwei separate Anträge erstellen.".into()
            }
            Self::EmptyWorkWeek => "Es muss mindestens ein Arbeitstag pro Woche ausgewählt sein.".into(),
            Self::ZeroAnnualEntitlement => "Der Jahresurlaub muss größer als 0 sein.".into(),
            Self::ApprovedExceedsEntitlement => {
                "Der bereits genehmigte Urlaub überschreitet den Jahresanspruch.".into()
            }
            Self::BothHalfDaysOnSingleDay => {
                "Ein einzelner Tag kann nicht gleichzeitig am Anfang und Ende als Halbtag markiert werden.".into()
            }
            Self::AzaOutsideRequestRange { date } => {
                format!(
                    "Der AZA-Tag {} liegt außerhalb des Antragszeitraums.",
                    date.format("%d.%m.%Y")
                )
            }
            Self::FullDayVocationalSchool { dates } => {
                let dates = dates.iter().map(|date| date.format("%d.%m.%Y").to_string()).collect::<Vec<_>>().join(", ");
                format!("An ganztägigen Berufsschultagen kann kein Urlaub genommen werden. Betroffene Termine: {dates}")
            }
            Self::ExceedsRemaining { .. } => {
                "Die beantragten Tage überschreiten den Resturlaub. Bitte Zeitraum anpassen.".into()
            }
            Self::DateRangeOverflow => "Der Antragszeitraum konnte nicht vollständig ausgewertet werden.".into(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CalculationWarning {
    CalendarFactsMissing { date: NaiveDate },
}

impl CalculationWarning {
    pub fn message(&self) -> String {
        match self {
            Self::CalendarFactsMissing { date } => {
                format!(
                    "Für {} fehlen Feiertags- oder Ferieninformationen.",
                    date.format("%d.%m.%Y")
                )
            }
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CalculationResult {
    pub days: Vec<DayEvaluation>,
    pub requested: DayUnits,
    pub annual_entitlement: DayUnits,
    pub already_approved: DayUnits,
    pub remaining_before: i64,
    pub remaining_after: i64,
    pub errors: Vec<CalculationError>,
    pub warnings: Vec<CalculationWarning>,
}

impl CalculationResult {
    pub fn has_errors(&self) -> bool {
        !self.errors.is_empty()
    }
}

pub fn calculate(request: &VacationRequest, calendar: &CalendarFacts) -> CalculationResult {
    let mut errors = Vec::new();
    let mut warnings = Vec::new();

    if request.start_date > request.end_date {
        errors.push(CalculationError::StartAfterEnd);
    } else if request.start_date.year() != request.end_date.year() {
        errors.push(CalculationError::CrossesCalendarYear);
    }
    if request.work_week.is_empty() {
        errors.push(CalculationError::EmptyWorkWeek);
    }
    if request.annual_entitlement == DayUnits::ZERO {
        errors.push(CalculationError::ZeroAnnualEntitlement);
    }
    if request.already_approved > request.annual_entitlement {
        errors.push(CalculationError::ApprovedExceedsEntitlement);
    }
    if request.start_date == request.end_date && request.start_half_day && request.end_half_day {
        errors.push(CalculationError::BothHalfDaysOnSingleDay);
    }
    for date in request.aza_dates.iter() {
        if *date < request.start_date || *date > request.end_date {
            errors.push(CalculationError::AzaOutsideRequestRange { date: *date });
        }
    }

    let mut days = Vec::new();
    let mut full_school_days = Vec::new();

    if request.start_date <= request.end_date {
        let mut date = request.start_date;
        loop {
            let day = evaluate_day(
                request,
                date,
                calendar.get(date),
                &mut warnings,
                &mut full_school_days,
            );
            days.push(day);
            if date == request.end_date {
                break;
            }
            match date.succ_opt() {
                Some(next) => date = next,
                None => {
                    errors.push(CalculationError::DateRangeOverflow);
                    break;
                }
            }
        }
    }

    if !full_school_days.is_empty() {
        errors.push(CalculationError::FullDayVocationalSchool {
            dates: full_school_days,
        });
    }

    let requested_half_units = days.iter().map(|day| day.counted.half_units()).sum::<u32>();
    let requested = DayUnits::from_half_units(requested_half_units);
    let remaining_before = i64::from(request.annual_entitlement.half_units())
        - i64::from(request.already_approved.half_units());
    let remaining_after = remaining_before - i64::from(requested.half_units());

    if remaining_before >= 0 && i64::from(requested.half_units()) > remaining_before {
        errors.push(CalculationError::ExceedsRemaining {
            requested,
            remaining_half_units: remaining_before,
        });
    }

    CalculationResult {
        days,
        requested,
        annual_entitlement: request.annual_entitlement,
        already_approved: request.already_approved,
        remaining_before,
        remaining_after,
        errors,
        warnings,
    }
}

fn evaluate_day(
    request: &VacationRequest,
    date: NaiveDate,
    context: Option<CalendarDayFacts>,
    warnings: &mut Vec<CalculationWarning>,
    full_school_days: &mut Vec<NaiveDate>,
) -> DayEvaluation {
    let context = match context {
        Some(context) => context,
        None => {
            warnings.push(CalculationWarning::CalendarFactsMissing { date });
            CalendarDayFacts::default()
        }
    };

    let weekday = date.weekday();
    let is_workday = request.work_week.contains(weekday);
    let is_start_half = request.start_half_day && date == request.start_date;
    let is_end_half = request.end_half_day && date == request.end_date;
    let is_aza = request.aza_dates.contains(date);
    let vocational_school = request.student_rules.school_type(weekday);
    let mut counted = if is_workday {
        DayUnits::ONE_DAY
    } else {
        DayUnits::ZERO
    };
    let mut reasons = Vec::new();

    if !is_workday {
        reasons.push(DayReason::NonWorkday);
    }

    let mut holiday_suppresses_school = false;
    match context.public_holiday {
        Some(PublicHolidayKind::Mandatory) => {
            counted = DayUnits::ZERO;
            holiday_suppresses_school = true;
            reasons.push(DayReason::MandatoryHoliday);
        }
        Some(PublicHolidayKind::Optional) if request.include_optional_holidays => {
            holiday_suppresses_school = true;
            reasons.push(DayReason::OptionalHolidayIncluded);
        }
        Some(PublicHolidayKind::Optional) => {
            reasons.push(DayReason::OptionalHolidayNotIncluded);
        }
        None => {}
    }

    if is_aza {
        counted = DayUnits::ZERO;
        reasons.push(DayReason::Aza);
    }

    if context.is_school_vacation {
        reasons.push(DayReason::SchoolVacation);
    }

    if !is_aza
        && !holiday_suppresses_school
        && !context.is_school_vacation
        && request.student_rules.active
    {
        match vocational_school {
            VocationalSchoolType::None => {}
            VocationalSchoolType::Half => {
                counted = counted.min(DayUnits::HALF_DAY);
                reasons.push(DayReason::HalfDayVocationalSchool);
            }
            VocationalSchoolType::Full => {
                counted = DayUnits::ZERO;
                full_school_days.push(date);
                reasons.push(DayReason::FullDayVocationalSchool);
            }
        }
    }

    if is_start_half {
        counted = counted.min(DayUnits::HALF_DAY);
        reasons.push(DayReason::StartHalfDay);
    }
    if is_end_half {
        counted = counted.min(DayUnits::HALF_DAY);
        reasons.push(DayReason::EndHalfDay);
    }
    if reasons.is_empty() {
        reasons.push(DayReason::OrdinaryWorkday);
    }

    DayEvaluation {
        date,
        weekday,
        is_workday,
        public_holiday: context.public_holiday,
        is_school_vacation: context.is_school_vacation,
        vocational_school,
        is_aza,
        start_half_day: is_start_half,
        end_half_day: is_end_half,
        counted,
        reasons,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn date(year: i32, month: u32, day: u32) -> NaiveDate {
        NaiveDate::from_ymd_opt(year, month, day).expect("valid test date")
    }

    fn request(start: NaiveDate, end: NaiveDate) -> VacationRequest {
        VacationRequest {
            start_date: start,
            end_date: end,
            start_half_day: false,
            end_half_day: false,
            work_week: WorkWeek::standard(),
            student_rules: StudentRules::default(),
            include_optional_holidays: false,
            annual_entitlement: DayUnits::from_half_units(60),
            already_approved: DayUnits::ZERO,
            aza_dates: AzaDates::default(),
        }
    }

    fn calendar(start: NaiveDate, end: NaiveDate) -> CalendarFacts {
        let mut facts = CalendarFacts::default();
        let mut current = start;
        while current <= end {
            facts.insert(current, CalendarDayFacts::default());
            if current == end {
                break;
            }
            current = current.succ_opt().expect("test range is representable");
        }
        facts
    }

    fn one_day_units(result: &CalculationResult, index: usize) -> u32 {
        result.days[index].counted.half_units()
    }

    #[test]
    fn req_calc_001_req_test_101_evaluates_every_single_workday() {
        let day = date(2026, 11, 9);
        let result = calculate(&request(day, day), &calendar(day, day));

        assert_eq!(result.days.len(), 1);
        assert_eq!(one_day_units(&result, 0), 2);
        assert_eq!(result.days[0].weekday, Weekday::Mon);
        assert!(result.days[0].is_workday);
        assert_eq!(
            result.days[0].reasons[0].message(),
            "Konfigurierter Arbeitstag."
        );
    }

    #[test]
    fn req_calc_002_req_test_102_weekend_does_not_consume_leave() {
        let start = date(2026, 11, 7);
        let end = date(2026, 11, 8);
        let result = calculate(&request(start, end), &calendar(start, end));

        assert_eq!(result.requested.half_units(), 0);
        assert_eq!(result.days.len(), 2);
        assert!(result.days.iter().all(|day| !day.is_workday));
    }

    #[test]
    fn req_calc_003_req_test_103_friday_to_monday_counts_two_days() {
        let start = date(2026, 11, 6);
        let end = date(2026, 11, 9);
        let result = calculate(&request(start, end), &calendar(start, end));

        assert_eq!(result.days.len(), 4);
        assert_eq!(result.requested.half_units(), 4);
    }

    #[test]
    fn req_calc_004_req_test_104_mandatory_holiday_is_zero() {
        let holiday = date(2026, 11, 10);
        let mut facts = calendar(holiday, holiday);
        facts.insert(
            holiday,
            CalendarDayFacts {
                public_holiday: Some(PublicHolidayKind::Mandatory),
                is_school_vacation: false,
            },
        );
        let result = calculate(&request(holiday, holiday), &facts);

        assert_eq!(result.requested.half_units(), 0);
        assert_eq!(
            result.days[0].public_holiday,
            Some(PublicHolidayKind::Mandatory)
        );
    }

    #[test]
    fn req_calc_005_req_test_105_optional_holiday_needs_activation_to_suppress_school() {
        let holiday = date(2026, 11, 11);
        let mut facts = calendar(holiday, holiday);
        facts.insert(
            holiday,
            CalendarDayFacts {
                public_holiday: Some(PublicHolidayKind::Optional),
                is_school_vacation: false,
            },
        );
        let mut enabled = request(holiday, holiday);
        enabled.include_optional_holidays = true;
        enabled.student_rules.active = true;
        enabled
            .student_rules
            .set_weekday(Weekday::Wed, VocationalSchoolType::Full);
        let enabled_result = calculate(&enabled, &facts);

        assert_eq!(enabled_result.requested.half_units(), 2);
        assert!(enabled_result.errors.is_empty());
        assert!(enabled_result.days[0]
            .reasons
            .contains(&DayReason::OptionalHolidayIncluded));

        let mut disabled = enabled;
        disabled.include_optional_holidays = false;
        let disabled_result = calculate(&disabled, &facts);

        assert_eq!(disabled_result.requested.half_units(), 0);
        assert!(disabled_result
            .errors
            .iter()
            .any(|error| matches!(error, CalculationError::FullDayVocationalSchool { .. })));
        assert!(disabled_result.days[0]
            .reasons
            .contains(&DayReason::OptionalHolidayNotIncluded));
    }

    #[test]
    fn req_calc_006_req_test_106_start_and_end_half_days_are_not_double_counted() {
        let start = date(2026, 11, 9);
        let end = date(2026, 11, 10);
        let mut first_half = request(start, end);
        first_half.start_half_day = true;
        let result = calculate(&first_half, &calendar(start, end));

        assert_eq!(one_day_units(&result, 0), 1);
        assert_eq!(one_day_units(&result, 1), 2);

        let mut last_half = request(start, end);
        last_half.end_half_day = true;
        let result = calculate(&last_half, &calendar(start, end));

        assert_eq!(one_day_units(&result, 0), 2);
        assert_eq!(one_day_units(&result, 1), 1);
    }

    #[test]
    fn req_calc_007_req_test_107_aza_overrides_holidays_and_school_block() {
        let start = date(2026, 11, 9);
        let end = date(2026, 11, 11);
        let mut facts = calendar(start, end);
        facts.insert(
            date(2026, 11, 10),
            CalendarDayFacts {
                public_holiday: Some(PublicHolidayKind::Mandatory),
                is_school_vacation: false,
            },
        );
        let mut request = request(start, end);
        request.student_rules.active = true;
        request
            .student_rules
            .set_weekday(Weekday::Mon, VocationalSchoolType::Full);
        request
            .student_rules
            .set_weekday(Weekday::Wed, VocationalSchoolType::Full);
        request.aza_dates.insert(start);
        request.aza_dates.insert(date(2026, 11, 10));
        request.aza_dates.insert(end);
        let result = calculate(&request, &facts);

        assert_eq!(result.requested.half_units(), 0);
        assert!(result.errors.is_empty());
        assert!(result.days.iter().all(|day| day.is_aza));
    }

    #[test]
    fn req_calc_008_req_test_108_full_and_half_vocational_school_are_distinct() {
        let half_day = date(2026, 11, 9);
        let mut half_request = request(half_day, half_day);
        half_request.student_rules.active = true;
        half_request
            .student_rules
            .set_weekday(Weekday::Mon, VocationalSchoolType::Half);
        let half_result = calculate(&half_request, &calendar(half_day, half_day));

        assert_eq!(half_result.requested.half_units(), 1);
        assert!(half_result.errors.is_empty());

        let full_day = date(2026, 11, 10);
        let mut full_request = request(full_day, full_day);
        full_request.student_rules.active = true;
        full_request
            .student_rules
            .set_weekday(Weekday::Tue, VocationalSchoolType::Full);
        let full_result = calculate(&full_request, &calendar(full_day, full_day));

        assert_eq!(full_result.requested.half_units(), 0);
        assert!(full_result.errors.iter().any(|error| matches!(
            error,
            CalculationError::FullDayVocationalSchool { dates } if dates == &vec![full_day]
        )));
    }

    #[test]
    fn req_calc_009_req_test_109_school_vacation_suppresses_vocational_school() {
        let day = date(2026, 11, 9);
        let mut facts = calendar(day, day);
        facts.insert(
            day,
            CalendarDayFacts {
                public_holiday: None,
                is_school_vacation: true,
            },
        );
        let mut request = request(day, day);
        request.student_rules.active = true;
        request
            .student_rules
            .set_weekday(Weekday::Mon, VocationalSchoolType::Full);
        let result = calculate(&request, &facts);

        assert_eq!(result.requested.half_units(), 2);
        assert!(result.errors.is_empty());
        assert!(result.days[0].is_school_vacation);
    }

    #[test]
    fn req_calc_009_req_test_109_school_vacation_boundary_only_suppresses_inside_range() {
        let start = date(2026, 8, 31);
        let end = date(2026, 9, 1);
        let mut facts = calendar(start, end);
        facts.insert(
            start,
            CalendarDayFacts {
                public_holiday: None,
                is_school_vacation: true,
            },
        );
        let mut request = request(start, end);
        request.student_rules.active = true;
        request
            .student_rules
            .set_weekday(Weekday::Mon, VocationalSchoolType::Full);
        request
            .student_rules
            .set_weekday(Weekday::Tue, VocationalSchoolType::Full);
        let result = calculate(&request, &facts);

        assert_eq!(one_day_units(&result, 0), 2);
        assert_eq!(one_day_units(&result, 1), 0);
        assert!(result.errors.iter().any(|error| matches!(
            error,
            CalculationError::FullDayVocationalSchool { dates } if dates == &vec![end]
        )));
    }

    #[test]
    fn req_calc_010_req_test_110_cross_year_is_rejected_and_leap_day_is_evaluated() {
        let start = date(2026, 12, 31);
        let end = date(2027, 1, 1);
        let cross_year = calculate(&request(start, end), &calendar(start, end));
        assert!(cross_year
            .errors
            .contains(&CalculationError::CrossesCalendarYear));
        assert_eq!(cross_year.days.len(), 2);

        let leap_start = date(2024, 2, 28);
        let leap_end = date(2024, 3, 1);
        let leap = calculate(
            &request(leap_start, leap_end),
            &calendar(leap_start, leap_end),
        );
        assert_eq!(leap.days.len(), 3);
        assert_eq!(leap.days[1].date, date(2024, 2, 29));
    }

    #[test]
    fn req_calc_011_req_test_111_custom_workweek_can_include_saturday() {
        let friday = date(2026, 11, 6);
        let saturday = date(2026, 11, 7);
        let mut request = request(friday, saturday);
        request.work_week = WorkWeek::from_weekdays([Weekday::Sat]);
        let result = calculate(&request, &calendar(friday, saturday));

        assert_eq!(result.requested.half_units(), 2);
        assert!(!result.days[0].is_workday);
        assert!(result.days[1].is_workday);
    }

    #[test]
    fn req_aza_001_req_test_112_113_duplicate_and_out_of_range_aza_are_handled() {
        let day = date(2026, 11, 9);
        let mut aza_dates = AzaDates::default();
        assert!(aza_dates.insert(day));
        assert!(!aza_dates.insert(day));

        let mut request = request(day, day);
        request.aza_dates.insert(date(2026, 11, 8));
        let result = calculate(&request, &calendar(day, day));
        assert!(result
            .errors
            .contains(&CalculationError::AzaOutsideRequestRange {
                date: date(2026, 11, 8),
            }));
        assert!(result
            .errors
            .iter()
            .any(|error| error.message().contains("08.11.2026")));
    }

    #[test]
    fn req_calc_011_req_test_114_exact_entitlement_is_allowed() {
        let day = date(2026, 11, 9);
        let mut request = request(day, day);
        request.annual_entitlement = DayUnits::ONE_DAY;
        let result = calculate(&request, &calendar(day, day));

        assert_eq!(result.remaining_after, 0);
        assert!(!result
            .errors
            .iter()
            .any(|error| matches!(error, CalculationError::ExceedsRemaining { .. })));
    }

    #[test]
    fn req_calc_008_approved_days_above_entitlement_are_rejected() {
        let day = date(2026, 11, 9);
        let mut request = request(day, day);
        request.annual_entitlement = DayUnits::ONE_DAY;
        request.already_approved = DayUnits::from_half_units(3);
        let result = calculate(&request, &calendar(day, day));

        assert!(result
            .errors
            .contains(&CalculationError::ApprovedExceedsEntitlement));
        assert_eq!(result.remaining_before, -1);
        assert_eq!(result.remaining_after, -3);
    }

    #[test]
    fn req_calc_008_req_test_115_half_unit_over_entitlement_is_rejected() {
        let start = date(2026, 11, 9);
        let end = date(2026, 11, 10);
        let mut request = request(start, end);
        request.annual_entitlement = DayUnits::ONE_DAY;
        request.start_half_day = true;
        let result = calculate(&request, &calendar(start, end));

        assert_eq!(result.requested.half_units(), 3);
        assert_eq!(result.remaining_after, -1);
        assert!(result.errors.iter().any(|error| matches!(
            error,
            CalculationError::ExceedsRemaining {
                remaining_half_units: 2,
                ..
            }
        )));
    }

    #[test]
    fn req_calc_005_single_day_cannot_be_half_at_both_edges() {
        let day = date(2026, 11, 9);
        let mut request = request(day, day);
        request.start_half_day = true;
        request.end_half_day = true;
        let result = calculate(&request, &calendar(day, day));

        assert!(result
            .errors
            .contains(&CalculationError::BothHalfDaysOnSingleDay));
        assert_eq!(result.requested.half_units(), 1);
    }

    #[test]
    fn req_calc_010_missing_calendar_facts_are_reported_as_warning() {
        let day = date(2026, 11, 9);
        let result = calculate(&request(day, day), &CalendarFacts::default());

        assert_eq!(
            result.warnings,
            vec![CalculationWarning::CalendarFactsMissing { date: day }]
        );
        assert_eq!(
            result.warnings[0].message(),
            "Für 09.11.2026 fehlen Feiertags- oder Ferieninformationen."
        );
        assert_eq!(result.requested.half_units(), 2);
    }

    #[test]
    fn req_calc_012_results_are_deterministic_for_identical_inputs() {
        let start = date(2026, 11, 6);
        let end = date(2026, 11, 9);
        let request = request(start, end);
        let facts = calendar(start, end);

        assert_eq!(calculate(&request, &facts), calculate(&request, &facts));
    }
}
