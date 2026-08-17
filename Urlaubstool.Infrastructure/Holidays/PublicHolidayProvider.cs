using Urlaubstool.Domain;

namespace Urlaubstool.Infrastructure.Holidays;

/// <summary>
/// Offline public holiday provider for Germany with state-specific rules.
/// Distinguishes between mandatory and optional holidays.
/// </summary>
public sealed class PublicHolidayProvider : IPublicHolidayProvider
{
    private static readonly HashSet<string> States = Bundeslaender.Codes.ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Mandatory holidays: universally recognized as non-workdays
    private static readonly HashSet<string> Reformationstag = new(["BB", "MV", "SN", "ST", "TH", "HB", "HH", "NI", "SH"], StringComparer.OrdinalIgnoreCase);
    
    // Optional holidays: may not be observed by all employers
    private static readonly HashSet<string> HeiligeDreiKoenige = new(["BW", "BY", "ST"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Frauentag = new(["BE", "MV"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Fronleichnam = new(["BW", "BY", "HE", "NW", "RP", "SL"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MariaHimmelfahrt = new(["SL"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SundayHolidays = new(["BB"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Allerheiligen = new(["BW", "BY", "NW", "RP", "SL"], StringComparer.OrdinalIgnoreCase);

    public bool IsPublicHoliday(DateOnly date, string state)
    {
        var st = state.ToUpperInvariant();
        if (st == "NRW")
        {
            st = "NW";
        }
        
        if (!States.Contains(st))
        {
            return false;
        }

        return IsMandatoryHoliday(date, st);
    }

    public bool IsOptionalPublicHoliday(DateOnly date, string state)
    {
        var st = state.ToUpperInvariant();
        if (st == "NRW")
        {
            st = "NW";
        }
        
        if (!States.Contains(st))
        {
            return false;
        }

        return IsOptionalHoliday(date, st);
    }

    /// <summary>
    /// Returns true only for mandatory holidays that universally block working days.
    /// </summary>
    private static bool IsMandatoryHoliday(DateOnly date, string state)
    {
        if (IsFixedNationwide(date))
        {
            return true;
        }

        if (IsMandatoryStateHoliday(date, state))
        {
            return true;
        }

        if (IsMandatoryMoveable(date, state))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true for optional holidays that may or may not be observed.
    /// </summary>
    private static bool IsOptionalHoliday(DateOnly date, string state)
    {
        // Optional fixed holidays
        if ((date.Month, date.Day) switch
        {
            (1, 6) when HeiligeDreiKoenige.Contains(state) => true,
            (3, 8) when Frauentag.Contains(state) => true,
            (8, 15) when MariaHimmelfahrt.Contains(state) => true,
            (11, 1) when Allerheiligen.Contains(state) => true,
            _ => false
        })
        {
            return true;
        }

        // Optional moveable holidays
        var easter = CalculateEasterSunday(date.Year);
        var fronleichnam = easter.AddDays(60);

        if (date == fronleichnam && Fronleichnam.Contains(state))
        {
            return true;
        }

        var pentecostSunday = easter.AddDays(49);
        if ((date == easter || date == pentecostSunday) && SundayHolidays.Contains(state))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks mandatory state-specific holidays (only Reformationstag qualifies).
    /// </summary>
    private static bool IsMandatoryStateHoliday(DateOnly date, string state)
    {
        return (date.Month, date.Day) switch
        {
            (9, 20) when state.Equals("TH", StringComparison.OrdinalIgnoreCase) => true,
            (10, 31) when Reformationstag.Contains(state) => true,
            _ => false
        } || IsBussUndBettag(date, state);
    }

    /// <summary>
    /// Checks mandatory moveable holidays (all mandatory except Fronleichnam and optional Sundays).
    /// </summary>
    private static bool IsMandatoryMoveable(DateOnly date, string state)
    {
        var easter = CalculateEasterSunday(date.Year);
        var karfreitag = easter.AddDays(-2);
        var ostermontag = easter.AddDays(1);
        var himmelfahrt = easter.AddDays(39);
        var pfingstmontag = easter.AddDays(50);

        return date == karfreitag || date == ostermontag || date == himmelfahrt || date == pfingstmontag;
    }

    private static bool IsFixedNationwide(DateOnly date) => (date.Month, date.Day) switch
    {
        (1, 1) => true,   // Neujahr
        (5, 1) => true,   // Tag der Arbeit
        (10, 3) => true,  // Tag der Deutschen Einheit
        (12, 25) => true, // 1. Weihnachtsfeiertag
        (12, 26) => true, // 2. Weihnachtsfeiertag
        _ => false
    };



    private static bool IsBussUndBettag(DateOnly date, string state)
    {
        if (!state.Equals("SN", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Wednesday before 23 November.
        var reference = new DateOnly(date.Year, 11, 23);
        var offset = ((int)reference.DayOfWeek - (int)DayOfWeek.Wednesday + 7) % 7;
        var daysBack = offset == 0 ? 7 : offset;
        var holiday = reference.AddDays(-daysBack);
        return date == holiday;
    }

    private static DateOnly CalculateEasterSunday(int year)
    {
        // Meeus/Jones/Butcher algorithm.
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }
}
