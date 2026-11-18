use chrono::{Datelike, Duration, NaiveDate, Weekday};
use urlaubstool_domain::PublicHolidayKind;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GermanState {
    BadenWuerttemberg,
    Bavaria,
    Berlin,
    Brandenburg,
    Bremen,
    Hamburg,
    Hesse,
    MecklenburgVorpommern,
    LowerSaxony,
    NorthRhineWestphalia,
    RhinelandPalatinate,
    Saarland,
    Saxony,
    SaxonyAnhalt,
    SchleswigHolstein,
    Thuringia,
}

impl GermanState {
    pub const ALL: [Self; 16] = [
        Self::BadenWuerttemberg,
        Self::Bavaria,
        Self::Berlin,
        Self::Brandenburg,
        Self::Bremen,
        Self::Hamburg,
        Self::Hesse,
        Self::MecklenburgVorpommern,
        Self::LowerSaxony,
        Self::NorthRhineWestphalia,
        Self::RhinelandPalatinate,
        Self::Saarland,
        Self::Saxony,
        Self::SaxonyAnhalt,
        Self::SchleswigHolstein,
        Self::Thuringia,
    ];

    pub const fn code(self) -> &'static str {
        match self {
            Self::BadenWuerttemberg => "BW",
            Self::Bavaria => "BY",
            Self::Berlin => "BE",
            Self::Brandenburg => "BB",
            Self::Bremen => "HB",
            Self::Hamburg => "HH",
            Self::Hesse => "HE",
            Self::MecklenburgVorpommern => "MV",
            Self::LowerSaxony => "NI",
            Self::NorthRhineWestphalia => "NW",
            Self::RhinelandPalatinate => "RP",
            Self::Saarland => "SL",
            Self::Saxony => "SN",
            Self::SaxonyAnhalt => "ST",
            Self::SchleswigHolstein => "SH",
            Self::Thuringia => "TH",
        }
    }

    pub const fn name(self) -> &'static str {
        match self {
            Self::BadenWuerttemberg => "Baden-Württemberg",
            Self::Bavaria => "Bayern",
            Self::Berlin => "Berlin",
            Self::Brandenburg => "Brandenburg",
            Self::Bremen => "Bremen",
            Self::Hamburg => "Hamburg",
            Self::Hesse => "Hessen",
            Self::MecklenburgVorpommern => "Mecklenburg-Vorpommern",
            Self::LowerSaxony => "Niedersachsen",
            Self::NorthRhineWestphalia => "Nordrhein-Westfalen",
            Self::RhinelandPalatinate => "Rheinland-Pfalz",
            Self::Saarland => "Saarland",
            Self::Saxony => "Sachsen",
            Self::SaxonyAnhalt => "Sachsen-Anhalt",
            Self::SchleswigHolstein => "Schleswig-Holstein",
            Self::Thuringia => "Thüringen",
        }
    }

    pub fn from_code(code: &str) -> Option<Self> {
        match code.trim().to_ascii_uppercase().as_str() {
            "BW" => Some(Self::BadenWuerttemberg),
            "BY" => Some(Self::Bavaria),
            "BE" => Some(Self::Berlin),
            "BB" => Some(Self::Brandenburg),
            "HB" => Some(Self::Bremen),
            "HH" => Some(Self::Hamburg),
            "HE" => Some(Self::Hesse),
            "MV" => Some(Self::MecklenburgVorpommern),
            "NI" => Some(Self::LowerSaxony),
            "NW" | "NRW" => Some(Self::NorthRhineWestphalia),
            "RP" => Some(Self::RhinelandPalatinate),
            "SL" => Some(Self::Saarland),
            "SN" => Some(Self::Saxony),
            "ST" => Some(Self::SaxonyAnhalt),
            "SH" => Some(Self::SchleswigHolstein),
            "TH" => Some(Self::Thuringia),
            _ => None,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct HolidayInfo {
    pub kind: PublicHolidayKind,
    pub name: &'static str,
}

pub fn public_holiday(date: NaiveDate, state: GermanState) -> Option<HolidayInfo> {
    let easter = easter_sunday(date.year());

    if let Some(name) = nationwide_fixed(date) {
        return Some(mandatory(name));
    }

    if let Some(easter) = easter {
        let moveable = [
            (easter - Duration::days(2), "Karfreitag"),
            (easter + Duration::days(1), "Ostermontag"),
            (easter + Duration::days(39), "Christi Himmelfahrt"),
            (easter + Duration::days(50), "Pfingstmontag"),
        ];
        if let Some((_, name)) = moveable.iter().find(|(holiday, _)| *holiday == date) {
            return Some(mandatory(name));
        }
    }

    if state == GermanState::Thuringia && (date.month(), date.day()) == (9, 20) {
        return Some(mandatory("Weltkindertag"));
    }
    if (date.month(), date.day()) == (10, 31) && observes_reformation_day(state) {
        return Some(mandatory("Reformationstag"));
    }
    if state == GermanState::Saxony && buß_und_bettag(date) {
        return Some(mandatory("Buß- und Bettag"));
    }

    if let Some(info) = state_fixed(date, state) {
        return Some(info);
    }
    if let Some(easter) = easter {
        if date == easter + Duration::days(60) {
            // Fronleichnam: landesweit in sechs Ländern, in Sachsen und Thüringen
            // nur in einzelnen Gemeinden (Entscheidung E8, 18.11.2026).
            if observes_corpus_christi(state) {
                return Some(mandatory("Fronleichnam"));
            }
            if matches!(state, GermanState::Saxony | GermanState::Thuringia) {
                return Some(optional("Fronleichnam (regional)"));
            }
        }
        if state == GermanState::Brandenburg
            && (date == easter || date == easter + Duration::days(49))
        {
            return Some(mandatory(if date == easter {
                "Ostersonntag"
            } else {
                "Pfingstsonntag"
            }));
        }
    }

    None
}

fn nationwide_fixed(date: NaiveDate) -> Option<&'static str> {
    match (date.month(), date.day()) {
        (1, 1) => Some("Neujahr"),
        (5, 1) => Some("Tag der Arbeit"),
        (10, 3) => Some("Tag der Deutschen Einheit"),
        (12, 25) => Some("1. Weihnachtstag"),
        (12, 26) => Some("2. Weihnachtstag"),
        _ => None,
    }
}

/// Landesfeiertage mit festem Datum. Gesetzlich im ganzen Land = `Mandatory`;
/// nur in einem Teil des Landes (Gemeinden/Städte) = `Optional`, zählt dann nur,
/// wenn optionale Feiertage eingeschaltet sind (Entscheidung E8, 18.11.2026).
fn state_fixed(date: NaiveDate, state: GermanState) -> Option<HolidayInfo> {
    let info = match (date.month(), date.day()) {
        (1, 6)
            if matches!(
                state,
                GermanState::BadenWuerttemberg | GermanState::Bavaria | GermanState::SaxonyAnhalt
            ) =>
        {
            mandatory("Heilige Drei Könige")
        }
        (3, 8)
            if matches!(
                state,
                GermanState::Berlin | GermanState::MecklenburgVorpommern
            ) =>
        {
            mandatory("Internationaler Frauentag")
        }
        (8, 8) if state == GermanState::Bavaria => {
            optional("Augsburger Friedensfest (nur Augsburg)")
        }
        (8, 15) if state == GermanState::Saarland => mandatory("Mariä Himmelfahrt"),
        (8, 15) if state == GermanState::Bavaria => {
            optional("Mariä Himmelfahrt (überwiegend katholische Gemeinden)")
        }
        (11, 1)
            if matches!(
                state,
                GermanState::BadenWuerttemberg
                    | GermanState::Bavaria
                    | GermanState::NorthRhineWestphalia
                    | GermanState::RhinelandPalatinate
                    | GermanState::Saarland
            ) =>
        {
            mandatory("Allerheiligen")
        }
        _ => return None,
    };
    Some(info)
}

fn observes_reformation_day(state: GermanState) -> bool {
    matches!(
        state,
        GermanState::Brandenburg
            | GermanState::MecklenburgVorpommern
            | GermanState::Saxony
            | GermanState::SaxonyAnhalt
            | GermanState::Thuringia
            | GermanState::Bremen
            | GermanState::Hamburg
            | GermanState::LowerSaxony
            | GermanState::SchleswigHolstein
    )
}

fn observes_corpus_christi(state: GermanState) -> bool {
    matches!(
        state,
        GermanState::BadenWuerttemberg
            | GermanState::Bavaria
            | GermanState::Hesse
            | GermanState::NorthRhineWestphalia
            | GermanState::RhinelandPalatinate
            | GermanState::Saarland
    )
}

fn buß_und_bettag(date: NaiveDate) -> bool {
    let Some(reference) = NaiveDate::from_ymd_opt(date.year(), 11, 23) else {
        return false;
    };
    let Some(mut day) = reference.pred_opt() else {
        return false;
    };
    loop {
        if day.weekday() == Weekday::Wed {
            return day == date;
        }
        if day.month() != 11 {
            return false;
        }
        let Some(previous) = day.pred_opt() else {
            return false;
        };
        day = previous;
    }
}

fn easter_sunday(year: i32) -> Option<NaiveDate> {
    let a = year % 19;
    let b = year / 100;
    let c = year % 100;
    let d = b / 4;
    let e = b % 4;
    let f = (b + 8) / 25;
    let g = (b - f + 1) / 3;
    let h = (19 * a + b - d - g + 15) % 30;
    let i = c / 4;
    let k = c % 4;
    let l = (32 + 2 * e + 2 * i - h - k) % 7;
    let m = (a + 11 * h + 22 * l) / 451;
    let month = (h + l - 7 * m + 114) / 31;
    let day = (h + l - 7 * m + 114) % 31 + 1;

    NaiveDate::from_ymd_opt(year, month as u32, day as u32)
}

fn mandatory(name: &'static str) -> HolidayInfo {
    HolidayInfo {
        kind: PublicHolidayKind::Mandatory,
        name,
    }
}

fn optional(name: &'static str) -> HolidayInfo {
    HolidayInfo {
        kind: PublicHolidayKind::Optional,
        name,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn date(year: i32, month: u32, day: u32) -> NaiveDate {
        NaiveDate::from_ymd_opt(year, month, day).expect("valid test date")
    }

    fn assert_state_scope(
        date: NaiveDate,
        included: &[GermanState],
        kind: PublicHolidayKind,
        name: &'static str,
    ) {
        for state in GermanState::ALL {
            let expected = if included.contains(&state) {
                Some(HolidayInfo { kind, name })
            } else {
                None
            };
            assert_eq!(public_holiday(date, state), expected, "{}", state.code());
        }
    }

    #[test]
    fn req_hol_001_all_sixteen_states_have_stable_codes_and_nationwide_holidays() {
        assert_eq!(GermanState::ALL.len(), 16);
        for state in GermanState::ALL {
            assert_eq!(GermanState::from_code(state.code()), Some(state));
            assert_eq!(
                public_holiday(date(2026, 1, 1), state),
                Some(mandatory("Neujahr"))
            );
        }
        assert_eq!(
            GermanState::from_code("NRW"),
            Some(GermanState::NorthRhineWestphalia)
        );
        assert_eq!(GermanState::from_code("XX"), None);
    }

    #[test]
    fn req_hol_001_movable_holidays_follow_easter_in_2026() {
        let state = GermanState::NorthRhineWestphalia;
        assert_eq!(
            public_holiday(date(2026, 4, 3), state),
            Some(mandatory("Karfreitag"))
        );
        assert_eq!(
            public_holiday(date(2026, 4, 6), state),
            Some(mandatory("Ostermontag"))
        );
        assert_eq!(
            public_holiday(date(2026, 5, 14), state),
            Some(mandatory("Christi Himmelfahrt"))
        );
        assert_eq!(
            public_holiday(date(2026, 5, 25), state),
            Some(mandatory("Pfingstmontag"))
        );
        assert_eq!(
            public_holiday(date(2026, 6, 4), state),
            Some(mandatory("Fronleichnam"))
        );
    }

    #[test]
    fn req_hol_001_nationwide_movable_holidays_apply_to_all_states() {
        let holidays = [
            (date(2026, 4, 3), "Karfreitag"),
            (date(2026, 4, 6), "Ostermontag"),
            (date(2026, 5, 14), "Christi Himmelfahrt"),
            (date(2026, 5, 25), "Pfingstmontag"),
        ];

        for state in GermanState::ALL {
            for (day, name) in holidays {
                assert_eq!(
                    public_holiday(day, state),
                    Some(mandatory(name)),
                    "{}",
                    state.code()
                );
            }
        }
    }

    #[test]
    fn req_hol_001_state_specific_holidays_keep_their_types() {
        assert_eq!(
            public_holiday(date(2026, 9, 20), GermanState::Thuringia),
            Some(mandatory("Weltkindertag"))
        );
        assert_eq!(
            public_holiday(date(2026, 10, 31), GermanState::Hamburg),
            Some(mandatory("Reformationstag"))
        );
        assert_eq!(
            public_holiday(date(2026, 10, 31), GermanState::Bavaria),
            None
        );
        assert_eq!(
            public_holiday(date(2026, 11, 18), GermanState::Saxony),
            Some(mandatory("Buß- und Bettag"))
        );
    }

    #[test]
    fn req_hol_001_buss_und_bettag_is_the_wednesday_before_november_23() {
        assert_eq!(
            public_holiday(date(2022, 11, 16), GermanState::Saxony),
            Some(mandatory("Buß- und Bettag"))
        );
        assert_eq!(
            public_holiday(date(2022, 11, 23), GermanState::Saxony),
            None
        );
    }

    #[test]
    fn req_hol_002_optional_days_are_only_regional_holidays() {
        // E8: Landesweit gesetzliche Feiertage zählen 0; optional sind nur Tage,
        // die nicht im ganzen Land gelten.
        assert_eq!(
            public_holiday(date(2026, 1, 6), GermanState::Bavaria),
            Some(mandatory("Heilige Drei Könige"))
        );
        assert_eq!(
            public_holiday(date(2026, 3, 8), GermanState::Berlin),
            Some(mandatory("Internationaler Frauentag"))
        );
        assert_eq!(public_holiday(date(2026, 3, 8), GermanState::Bavaria), None);
        assert_eq!(
            public_holiday(date(2026, 8, 15), GermanState::Bavaria),
            Some(optional(
                "Mariä Himmelfahrt (überwiegend katholische Gemeinden)"
            ))
        );
        assert_eq!(
            public_holiday(date(2026, 8, 8), GermanState::Bavaria),
            Some(optional("Augsburger Friedensfest (nur Augsburg)"))
        );
        assert_eq!(
            public_holiday(date(2026, 6, 4), GermanState::Saxony),
            Some(optional("Fronleichnam (regional)"))
        );
        assert_eq!(
            public_holiday(date(2026, 6, 4), GermanState::Thuringia),
            Some(optional("Fronleichnam (regional)"))
        );
        assert_eq!(public_holiday(date(2026, 6, 4), GermanState::Berlin), None);
    }

    #[test]
    fn req_hol_001_002_statewide_holidays_are_mandatory_in_their_states() {
        use GermanState as State;

        assert_state_scope(
            date(2026, 1, 6),
            &[
                State::BadenWuerttemberg,
                State::Bavaria,
                State::SaxonyAnhalt,
            ],
            PublicHolidayKind::Mandatory,
            "Heilige Drei Könige",
        );
        assert_state_scope(
            date(2026, 3, 8),
            &[State::Berlin, State::MecklenburgVorpommern],
            PublicHolidayKind::Mandatory,
            "Internationaler Frauentag",
        );
        assert_state_scope(
            date(2026, 10, 31),
            &[
                State::Brandenburg,
                State::MecklenburgVorpommern,
                State::Saxony,
                State::SaxonyAnhalt,
                State::Thuringia,
                State::Bremen,
                State::Hamburg,
                State::LowerSaxony,
                State::SchleswigHolstein,
            ],
            PublicHolidayKind::Mandatory,
            "Reformationstag",
        );
        for state in [
            State::BadenWuerttemberg,
            State::Bavaria,
            State::Hesse,
            State::NorthRhineWestphalia,
            State::RhinelandPalatinate,
            State::Saarland,
        ] {
            assert_eq!(
                public_holiday(date(2026, 6, 4), state),
                Some(mandatory("Fronleichnam")),
                "{}",
                state.code()
            );
        }
        assert_state_scope(
            date(2026, 11, 1),
            &[
                State::BadenWuerttemberg,
                State::Bavaria,
                State::NorthRhineWestphalia,
                State::RhinelandPalatinate,
                State::Saarland,
            ],
            PublicHolidayKind::Mandatory,
            "Allerheiligen",
        );
        assert_eq!(
            public_holiday(date(2026, 8, 15), State::Saarland),
            Some(mandatory("Mariä Himmelfahrt"))
        );
        assert_eq!(public_holiday(date(2026, 8, 15), State::Hesse), None);
        assert_state_scope(
            date(2026, 4, 5),
            &[State::Brandenburg],
            PublicHolidayKind::Mandatory,
            "Ostersonntag",
        );
        assert_state_scope(
            date(2026, 5, 24),
            &[State::Brandenburg],
            PublicHolidayKind::Mandatory,
            "Pfingstsonntag",
        );
    }
}
