#![forbid(unsafe_code)]

mod german_public;
mod school_ics;

pub use german_public::{public_holiday, GermanState, HolidayInfo};
pub use school_ics::{parse_kmk_ics, SchoolBreak, SchoolHolidayCalendar, SchoolHolidayImportError};
