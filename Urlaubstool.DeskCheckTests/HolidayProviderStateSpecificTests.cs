using System;
using FluentAssertions;
using Urlaubstool.Infrastructure.Holidays;
using Xunit;

namespace Urlaubstool.DeskCheckTests;

public class HolidayProviderStateSpecificTests
{
    private readonly PublicHolidayProvider _provider = new();

    [Fact]
    public void IsPublicHoliday_ReturnTrue_ForThuringiaWorldChildrensDay()
    {
        // TH: Weltkindertag 20.09.
        var date = new DateOnly(2026, 9, 20);
        _provider.IsPublicHoliday(date, "TH").Should().BeTrue("Weltkindertag is a holiday in TH");
        _provider.IsPublicHoliday(date, "BE").Should().BeFalse("Weltkindertag is NOT a holiday in BE");
    }

    [Fact]
    public void IsPublicHoliday_ReturnTrue_ForBrandenburgEasterSunday()
    {
        // BB: Ostersonntag
        // Easter 2026 is April 5th
        var date = new DateOnly(2026, 4, 5);
        _provider.IsPublicHoliday(date, "BB").Should().BeTrue("Ostersonntag is a holiday in BB");
        _provider.IsPublicHoliday(date, "NW").Should().BeFalse("Ostersonntag is NOT a holiday in NW");
    }

    [Fact]
    public void IsPublicHoliday_ReturnTrue_ForBrandenburgWhitSunday()
    {
        // BB: Pfingstsonntag
        // Easter 2026: April 5th -> Pfingsten: +49 days -> May 24th
        var date = new DateOnly(2026, 5, 24);
        _provider.IsPublicHoliday(date, "BB").Should().BeTrue("Pfingstsonntag is a holiday in BB");
        _provider.IsPublicHoliday(date, "HE").Should().BeFalse("Pfingstsonntag is NOT a holiday in HE");
    }

    [Fact]
    public void IsPublicHoliday_ReturnTrue_ForSaarlandAssumptionDay()
    {
        // SL: Mariä Himmelfahrt (15.08.)
        var date = new DateOnly(2026, 8, 15);
        _provider.IsPublicHoliday(date, "SL").Should().BeTrue("Mariä Himmelfahrt is a holiday in SL");
    }

    [Fact]
    public void IsPublicHoliday_ReturnFalse_ForBavariaAssumptionDay_ConservativeScale()
    {
        // BY: Mariä Himmelfahrt should be FALSE on state level
        var date = new DateOnly(2026, 8, 15);
        _provider.IsPublicHoliday(date, "BY").Should().BeFalse("Mariä Himmelfahrt is municipality-dependent in BY, so state-wide check returns false");
    }
}
