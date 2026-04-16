using Urlaubstool.Domain;

namespace Urlaubstool.Infrastructure.Holidays;

/// <summary>
/// Offline public holiday provider for Germany with state-specific rules.
/// </summary>
public sealed class PublicHolidayProvider : IPublicHolidayProvider
{
    private static readonly HashSet<string> States = Bundeslaender.Codes.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> HeiligeDreiKoenige = new(["BW", "BY", "ST"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Frauentag = new(["BE", "MV"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Fronleichnam = new(["BW", "BY", "HE", "NW", "RP", "SL"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MariaHimmelfahrt = new(["SL"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SundayHolidays = new(["BB"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Reformationstag = new(["BB", "MV", "SN", "ST", "TH", "HB", "HH", "NI", "SH"], StringComparer.OrdinalIgnoreCase);
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

        if (IsFixedNationwide(date) || IsFixedState(date, st) || IsMoveable(date, st))
        {
            return true;
        }

        return false;
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

    private static bool IsFixedState(DateOnly date, string state)
    {
        return ((date.Month, date.Day) switch
        {
            (1, 6) when HeiligeDreiKoenige.Contains(state) => true,
            (3, 8) when Frauentag.Contains(state) => true,
            (8, 15) when MariaHimmelfahrt.Contains(state) => true,
            (9, 20) when state.Equals("TH", StringComparison.OrdinalIgnoreCase) => true,
            (10, 31) when Reformationstag.Contains(state) => true,
            (11, 1) when Allerheiligen.Contains(state) => true,
            _ => false
        }) || IsBussUndBettag(date, state);
    }

    private static bool IsMoveable(DateOnly date, string state)
    {
        var easter = CalculateEasterSunday(date.Year);
        var pentecostSunday = easter.AddDays(49);

        // Some states consider the actual Sunday (Easter / Pentecost) as a public holiday
        if ((date == easter || date == pentecostSunday) && SundayHolidays.Contains(state))
        {
            return true;
        }
        var karfreitag = easter.AddDays(-2);
        var ostermontag = easter.AddDays(1);
        var himmelfahrt = easter.AddDays(39);
        var pfingstmontag = easter.AddDays(50);
        var fronleichnam = easter.AddDays(60);

        if (date == karfreitag || date == ostermontag || date == himmelfahrt || date == pfingstmontag)
        {
            return true;
        }

        if (date == fronleichnam && Fronleichnam.Contains(state))
        {
            return true;
        }

        return false;
    }

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
