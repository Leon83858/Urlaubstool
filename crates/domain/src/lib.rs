#![forbid(unsafe_code)]

mod calculation;
mod status;

pub use calculation::{
    calculate, AzaDates, CalculationError, CalculationResult, CalculationWarning, CalendarDayFacts,
    CalendarFacts, DayEvaluation, DayReason, DayUnits, PublicHolidayKind, StudentRules,
    VacationRequest, VocationalSchoolType, WorkWeek,
};
pub use status::{validate_transition, RequestStatus, StatusTransitionError};
