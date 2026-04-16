namespace Urlaubstool.Domain;

/// <summary>
/// Provides offline public holiday data per state.
/// </summary>
public interface IPublicHolidayProvider
{
    bool IsPublicHoliday(DateOnly date, string state);
}

/// <summary>
/// Provides offline school holiday data per state.
/// </summary>
public interface ISchoolHolidayProvider
{
    bool IsSchoolHoliday(DateOnly date, string state);
}
