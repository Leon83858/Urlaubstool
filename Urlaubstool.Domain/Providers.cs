namespace Urlaubstool.Domain;

/// <summary>
/// Provides offline public holiday data per state.
/// </summary>
public interface IPublicHolidayProvider
{
    /// <summary>
    /// Checks if a date is a mandatory public holiday that blocks vacation requests.
    /// Only returns true for holidays that are universally recognized as non-workdays.
    /// </summary>
    bool IsPublicHoliday(DateOnly date, string state);

    /// <summary>
    /// Checks if a date is an optional/discretionary public holiday.
    /// These holidays may or may not be observed by employers.
    /// </summary>
    bool IsOptionalPublicHoliday(DateOnly date, string state);
}

/// <summary>
/// Provides offline school holiday data per state.
/// </summary>
public interface ISchoolHolidayProvider
{
    /// <summary>
    /// Checks if a date is a school holiday (on a working day only, not weekends).
    /// School holidays block vacation for vocational students.
    /// </summary>
    bool IsSchoolHoliday(DateOnly date, string state);
}
