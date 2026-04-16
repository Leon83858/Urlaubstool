namespace Urlaubstool.Domain;

/// <summary>
/// Status values for persisted vacation requests in the ledger.
/// </summary>
public enum VacationRequestStatus
{
    Draft,
    Exported,
    Approved,
    Rejected,
    Archived,
    Deleted
}

/// <summary>
/// Vocational school occupancy per weekday for student mode.
/// </summary>
public enum VocationalSchoolDayType
{
    None,
    Half,
    Full
}
