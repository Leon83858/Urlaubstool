using System.Collections.Immutable;

namespace Urlaubstool.Domain;

/// <summary>
/// Student-related parameters that influence vacation calculation.
/// </summary>
public sealed record StudentParameters(
    bool Active,
    string? State,
    IReadOnlyDictionary<DayOfWeek, VocationalSchoolDayType> VocationalSchoolDays);

/// <summary>
/// Domain input describing a vacation request.
/// </summary>
public sealed record VacationRequest(
    DateOnly StartDate,
    DateOnly EndDate,
    bool StartHalfDay,
    bool EndHalfDay,
    ISet<DayOfWeek> WorkdaysOfWeek,
    StudentParameters Student,
    string? State,
    decimal AnnualEntitlement,
    decimal AlreadyApprovedThisYear,
    int Year,
    HashSet<DateOnly> AzaDates); // AZA-Tage (Arbeitszeitausgleich/Überstundenabbau) that don't count as vacation days

/// <summary>
/// Per-day evaluation result.
/// </summary>
public sealed record DayEvaluation(
    DateOnly Date,
    DayOfWeek DayOfWeek,
    bool IsWorkday,
    bool IsPublicHoliday,
    bool IsSchoolHoliday,
    VocationalSchoolDayType VocationalSchool,
    decimal CountedValue,
    IReadOnlyList<string> Messages)
{
    public string MessageSummary => string.Join(", ", Messages);
}

/// <summary>
/// Aggregated calculation result for a request.
/// </summary>
public sealed record CalculationResult(
    decimal TotalDays,
    IReadOnlyList<DayEvaluation> PerDay,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// Summary snapshot for entitlement accounting.
/// </summary>
public sealed record EntitlementSnapshot(
    decimal AnnualEntitlement,
    decimal AlreadyApproved,
    decimal Remaining)
{
    public static EntitlementSnapshot From(decimal annual, decimal approved)
    {
        var remaining = annual - approved;
        return new EntitlementSnapshot(annual, approved, remaining);
    }
}

public static class Bundeslaender
{
    /// <summary>
    /// Supported German states for holiday lookup.
    /// </summary>
    public static readonly ImmutableArray<string> Codes =
        ["BW", "BY", "BE", "BB", "HB", "HH", "HE", "MV", "NI", "NW", "RP", "SL", "SN", "ST", "SH", "TH"];
}
