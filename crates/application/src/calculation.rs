use chrono::{Datelike, NaiveDate, Weekday};
use serde::Serialize;
use urlaubstool_domain::{
    calculate, AzaDates, CalendarDayFacts, CalendarFacts, DayUnits, PublicHolidayKind,
    StudentRules, VacationRequest, VocationalSchoolType, WorkWeek,
};
use urlaubstool_holidays::{public_holiday, GermanState};

/// Berufsschule und Schulferien für die Berechnung (REQ-CALC-006, REQ-CALC-007).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct StudentCalendar {
    /// Berufsschulmodus der Einstellungen.
    pub active: bool,
    /// Montag bis Sonntag: 0 = keine, 1 = halbtags, 2 = ganztags.
    pub vocational_school_week: [u8; 7],
    /// Importierte Schulferien des Bundeslands als halboffene Zeiträume
    /// `[Beginn, Ende)`. `None`: Es wurden keine Ferien importiert.
    pub school_breaks: Option<Vec<(NaiveDate, NaiveDate)>>,
}

impl StudentCalendar {
    /// Ferien gelten für ein Jahr als vorhanden, wenn ein importierter Zeitraum
    /// in diesem Jahr beginnt oder endet.
    fn covers_year(&self, year: i32) -> bool {
        self.school_breaks.as_ref().is_some_and(|breaks| {
            breaks.iter().any(|(start, end_exclusive)| {
                start.year() == year
                    || end_exclusive
                        .pred_opt()
                        .is_some_and(|last| last.year() == year)
            })
        })
    }

    fn is_school_vacation(&self, date: NaiveDate) -> bool {
        self.school_breaks.as_ref().is_some_and(|breaks| {
            breaks
                .iter()
                .any(|(start, end_exclusive)| date >= *start && date < *end_exclusive)
        })
    }
}

#[derive(Debug, Clone, Default)]
pub struct VacationCalculationInput {
    pub start_date: String,
    pub end_date: String,
    pub start_half_day: bool,
    pub end_half_day: bool,
    pub workdays: Vec<bool>,
    pub federal_state: String,
    pub annual_entitlement_half_units: u32,
    pub already_approved_half_units: u32,
    pub aza_dates: Vec<String>,
    pub include_optional_holidays: bool,
    pub student: StudentCalendar,
}

pub struct VacationCalculationOutput {
    pub requested_half_units: u32,
    pub annual_entitlement_half_units: u32,
    pub already_approved_half_units: u32,
    pub remaining_before_half_units: i64,
    pub remaining_after_half_units: i64,
    pub errors: Vec<String>,
    pub warnings: Vec<String>,
    pub days: Vec<VacationDayOutput>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct VacationDayOutput {
    pub date: String,
    pub weekday: String,
    pub is_workday: bool,
    pub public_holiday: Option<String>,
    pub is_school_vacation: Option<bool>,
    pub vocational_school: Option<String>,
    pub is_aza: bool,
    pub start_half_day: bool,
    pub end_half_day: bool,
    pub counted_half_units: u32,
    pub reasons: Vec<String>,
}

pub fn calculate_vacation(
    input: VacationCalculationInput,
) -> Result<VacationCalculationOutput, String> {
    let start_date = NaiveDate::parse_from_str(&input.start_date, "%Y-%m-%d")
        .map_err(|_| "Ungültiges Startdatum.")?;
    let end_date = NaiveDate::parse_from_str(&input.end_date, "%Y-%m-%d")
        .map_err(|_| "Ungültiges Enddatum.")?;
    if start_date > end_date {
        return Err("Das Startdatum darf nicht nach dem Enddatum liegen.".into());
    }
    if start_date.year() != end_date.year() {
        return Err(
            "Zeitraum überschreitet das Kalenderjahr. Bitte zwei separate Anträge erstellen."
                .into(),
        );
    }
    if input.workdays.len() != 7 {
        return Err("Die Arbeitswoche muss sieben Wochentage enthalten.".into());
    }
    let state =
        GermanState::from_code(&input.federal_state).ok_or("Bitte ein Bundesland auswählen.")?;
    let weekdays = [
        Weekday::Mon,
        Weekday::Tue,
        Weekday::Wed,
        Weekday::Thu,
        Weekday::Fri,
        Weekday::Sat,
        Weekday::Sun,
    ];
    let work_week = WorkWeek::from_weekdays(
        weekdays
            .into_iter()
            .zip(input.workdays)
            .filter_map(|(day, selected)| selected.then_some(day)),
    );
    let mut student_rules = StudentRules::default();
    student_rules.active = input.student.active;
    for (weekday, value) in weekdays
        .into_iter()
        .zip(input.student.vocational_school_week)
    {
        let school_type = match value {
            0 => VocationalSchoolType::None,
            1 => VocationalSchoolType::Half,
            2 => VocationalSchoolType::Full,
            _ => return Err("Ungültige Berufsschulangabe in den Einstellungen.".into()),
        };
        student_rules.set_weekday(weekday, school_type);
    }
    let vacation_known = input.student.covers_year(start_date.year());
    let mut warnings = Vec::new();
    if input.student.active && !vacation_known {
        warnings.push(format!(
            "Für {} {} sind keine Schulferien importiert. Berufsschultage werden ohne Ferienprüfung bewertet; bitte den KMK-Ferienkalender in den Einstellungen importieren.",
            state.name(),
            start_date.year()
        ));
    }
    let mut aza_dates = AzaDates::default();
    for value in input.aza_dates {
        let date =
            NaiveDate::parse_from_str(&value, "%Y-%m-%d").map_err(|_| "Ungültiges AZA-Datum.")?;
        if !aza_dates.insert(date) {
            return Err("Ein AZA-Datum darf nur einmal ausgewählt werden.".into());
        }
    }
    let request = VacationRequest {
        start_date,
        end_date,
        start_half_day: input.start_half_day,
        end_half_day: input.end_half_day,
        work_week,
        student_rules,
        include_optional_holidays: input.include_optional_holidays,
        annual_entitlement: DayUnits::from_half_units(input.annual_entitlement_half_units),
        already_approved: DayUnits::from_half_units(input.already_approved_half_units),
        aza_dates,
    };
    let mut calendar = CalendarFacts::default();
    let mut date = start_date;
    loop {
        calendar.insert(
            date,
            CalendarDayFacts {
                public_holiday: public_holiday(date, state).map(|holiday| holiday.kind),
                is_school_vacation: vacation_known && input.student.is_school_vacation(date),
            },
        );
        if date == end_date {
            break;
        }
        date = date
            .succ_opt()
            .ok_or("Der Antragszeitraum konnte nicht ausgewertet werden.")?;
    }
    let result = calculate(&request, &calendar);
    Ok(VacationCalculationOutput {
        requested_half_units: result.requested.half_units(),
        annual_entitlement_half_units: result.annual_entitlement.half_units(),
        already_approved_half_units: result.already_approved.half_units(),
        remaining_before_half_units: result.remaining_before,
        remaining_after_half_units: result.remaining_after,
        errors: result.errors.iter().map(|error| error.message()).collect(),
        warnings,
        days: result
            .days
            .iter()
            .map(|day| VacationDayOutput {
                date: day.date.to_string(),
                weekday: match day.weekday {
                    Weekday::Mon => "Montag",
                    Weekday::Tue => "Dienstag",
                    Weekday::Wed => "Mittwoch",
                    Weekday::Thu => "Donnerstag",
                    Weekday::Fri => "Freitag",
                    Weekday::Sat => "Samstag",
                    Weekday::Sun => "Sonntag",
                }
                .into(),
                is_workday: day.is_workday,
                public_holiday: day.public_holiday.map(|kind| {
                    match kind {
                        PublicHolidayKind::Mandatory => "Gesetzlich",
                        PublicHolidayKind::Optional => "Optional",
                    }
                    .into()
                }),
                is_school_vacation: vacation_known.then_some(day.is_school_vacation),
                vocational_school: input.student.active.then(|| {
                    match day.vocational_school {
                        VocationalSchoolType::None => "Keine",
                        VocationalSchoolType::Half => "Halbtags",
                        VocationalSchoolType::Full => "Ganztags",
                    }
                    .to_owned()
                }),
                is_aza: day.is_aza,
                start_half_day: day.start_half_day,
                end_half_day: day.end_half_day,
                counted_half_units: day.counted.half_units(),
                reasons: day
                    .reasons
                    .iter()
                    .map(|reason| reason.message().to_string())
                    .collect(),
            })
            .collect(),
    })
}

/// Ein Kalendertag mit denselben Kalenderfakten, die auch die Berechnung nutzt (REQ-UI-006).
#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CalendarDayOutput {
    pub date: String,
    /// Wochentag ist laut Einstellungen ein Arbeitstag.
    pub is_workday: bool,
    /// Name des Feiertags im eingestellten Bundesland.
    pub public_holiday: Option<String>,
    /// Feiertag ist nur optional (zählt nur bei aktivierter Option als arbeitsfrei).
    pub holiday_is_optional: bool,
    /// `None`, wenn für das Jahr keine Schulferien importiert sind.
    pub is_school_vacation: Option<bool>,
    /// Wirksame Berufsschule („Halbtags“/„Ganztags“) nach der Regelpriorität E3.
    pub vocational_school: Option<String>,
}

/// Kalenderfakten eines ganzen Jahres für die Kalenderansicht.
pub fn calendar_year(
    year: i32,
    federal_state: &str,
    workdays: &[bool],
    include_optional_holidays: bool,
    student: &StudentCalendar,
) -> Result<Vec<CalendarDayOutput>, String> {
    if workdays.len() != 7 {
        return Err("Die Arbeitswoche muss sieben Wochentage enthalten.".into());
    }
    let state = GermanState::from_code(federal_state).ok_or("Bitte ein Bundesland auswählen.")?;
    let first = NaiveDate::from_ymd_opt(year, 1, 1).ok_or("Ungültiges Kalenderjahr.")?;
    let vacation_known = student.covers_year(year);
    let mut days = Vec::with_capacity(366);
    let mut date = first;
    while date.year() == year {
        let weekday = date.weekday().num_days_from_monday() as usize;
        let holiday = public_holiday(date, state);
        let holiday_free = holiday.is_some_and(|info| {
            info.kind == PublicHolidayKind::Mandatory || include_optional_holidays
        });
        let is_vacation = vacation_known && student.is_school_vacation(date);
        let school = student.vocational_school_week[weekday];
        // Wie in der Berechnung (und der Altanwendung) unabhängig davon, ob der
        // Wochentag ein Arbeitstag ist.
        let vocational_school = (student.active && !holiday_free && !is_vacation && school > 0)
            .then(|| if school == 1 { "Halbtags" } else { "Ganztags" }.to_owned());
        days.push(CalendarDayOutput {
            date: date.to_string(),
            is_workday: workdays[weekday],
            public_holiday: holiday.map(|info| info.name.to_owned()),
            holiday_is_optional: holiday
                .is_some_and(|info| info.kind == PublicHolidayKind::Optional),
            is_school_vacation: vacation_known.then_some(is_vacation),
            vocational_school,
        });
        date = match date.succ_opt() {
            Some(next) => next,
            None => break,
        };
    }
    Ok(days)
}
