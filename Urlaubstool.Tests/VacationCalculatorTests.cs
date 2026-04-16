using FluentAssertions;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.Holidays;

namespace Urlaubstool.Tests;

public class VacationCalculatorTests
{
    private static VacationCalculator BuildCalculator(IPublicHolidayProvider? publicProvider = null, ISchoolHolidayProvider? schoolProvider = null)
    {
        return new VacationCalculator(publicProvider ?? new NoHolidayProvider(), schoolProvider ?? new NoSchoolHolidayProvider());
    }

    [Fact]
    public void Weekend_is_excluded()
    {
        var calc = BuildCalculator();
        var request = CreateRequest(new DateOnly(2025, 1, 3), new DateOnly(2025, 1, 5), workdays: new HashSet<DayOfWeek> { DayOfWeek.Monday });
        var result = calc.Calculate(request);
        result.TotalDays.Should().Be(0m);
    }

    [Fact]
    public void Public_holiday_is_excluded()
    {
        var holidayDate = new DateOnly(2025, 5, 1);
        var calc = BuildCalculator(new StubPublicHolidayProvider(holidayDate));
        var request = CreateRequest(holidayDate, holidayDate, state: "NW");
        var result = calc.Calculate(request);
        result.TotalDays.Should().Be(0m);
    }

    [Fact]
    public void Student_full_school_day_blocks()
    {
        var calc = BuildCalculator();
        var vocational = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => VocationalSchoolDayType.Full);
        var request = CreateRequest(new DateOnly(2025, 1, 6), new DateOnly(2025, 1, 6), studentActive: true, vocational: vocational);
        var result = calc.Calculate(request);
        result.Errors.Should().NotBeEmpty();
        result.TotalDays.Should().Be(0m);
    }

    [Fact]
    public void Student_half_day_caps_to_point_five()
    {
        var calc = BuildCalculator();
        var vocational = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => VocationalSchoolDayType.None);
        vocational[DayOfWeek.Monday] = VocationalSchoolDayType.Half;
        var request = CreateRequest(new DateOnly(2025, 1, 6), new DateOnly(2025, 1, 6), studentActive: true, vocational: vocational);
        var result = calc.Calculate(request);
        result.TotalDays.Should().Be(0.5m);
    }

    [Fact]
    public void School_holiday_ignores_vocational_rules()
    {
        var schoolProvider = new StubSchoolHolidayProvider(new DateOnly(2025, 1, 6));
        var vocational = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => VocationalSchoolDayType.Full);
        var calc = BuildCalculator(schoolProvider: schoolProvider);
        var request = CreateRequest(new DateOnly(2025, 1, 6), new DateOnly(2025, 1, 6), studentActive: true, vocational: vocational);
        var result = calc.Calculate(request);
        result.Errors.Should().BeEmpty();
        result.TotalDays.Should().Be(1m);
    }

    [Fact]
    public void Half_day_only_on_edges_enforced()
    {
        var calc = BuildCalculator();
        // Test: Single day with both StartHalf and EndHalf should error
        var request = CreateRequest(new DateOnly(2025, 1, 6), new DateOnly(2025, 1, 6), startHalf: true, endHalf: true);
        var result = calc.Calculate(request);
        result.Errors.Should().NotBeEmpty().And.Contain(e => e.Contains("einzelnen Tag"));
    }

    [Fact]
    public void Cross_year_range_blocks()
    {
        var calc = BuildCalculator();
        var request = CreateRequest(new DateOnly(2025, 12, 31), new DateOnly(2026, 1, 2));
        var result = calc.Calculate(request);
        result.Errors.Should().Contain(e => e.Contains("Kalenderjahr"));
    }

    [Fact]
    public void Remaining_days_insufficient_blocks()
    {
        var calc = BuildCalculator();
        var request = CreateRequest(new DateOnly(2025, 1, 6), new DateOnly(2025, 1, 7), annual: 1m, approved: 1m);
        var result = calc.Calculate(request);
        result.Errors.Should().Contain(e => e.Contains("Resturlaub"));
    }

    [Fact]
    public void Buss_und_Bettag_known_dates()
    {
        var provider = new PublicHolidayProvider();
        provider.IsPublicHoliday(new DateOnly(2024, 11, 20), "SN").Should().BeTrue();
        provider.IsPublicHoliday(new DateOnly(2025, 11, 19), "SN").Should().BeTrue();
    }

    [Fact]
    public void School_holiday_cross_year_lookup()
    {
        var provider = new SchoolHolidayProvider();
        provider.IsSchoolHoliday(new DateOnly(2025, 1, 2), "NW").Should().BeTrue();
    }

    [Fact]
    public void AzaDays_count_as_zero_vacation_days()
    {
        var calc = BuildCalculator();
        var azaDates = new HashSet<DateOnly> { new DateOnly(2025, 1, 6) };
        var request = CreateRequest(new DateOnly(2025, 1, 6), new DateOnly(2025, 1, 8), azaDates: azaDates);
        var result = calc.Calculate(request);
        result.TotalDays.Should().Be(2m); // Only Jan 7-8 count, Jan 6 is AZA day (Arbeitszeitausgleich)
        result.PerDay.First(d => d.Date == new DateOnly(2025, 1, 6)).CountedValue.Should().Be(0m);
    }

    [Fact]
    public void AzaDays_two_day_range_one_aza_gives_one_day()
    {
        // User scenario: March 2-3, 2026 both workdays, AZA on March 2 → should give 1 day total
        var calc = BuildCalculator();
        var azaDates = new HashSet<DateOnly> { new DateOnly(2026, 3, 2) };
        var request = CreateRequest(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 3), azaDates: azaDates);
        var result = calc.Calculate(request);
        result.TotalDays.Should().Be(1m); // Only March 3 counts; March 2 is AZA
        result.PerDay.First(d => d.Date == new DateOnly(2026, 3, 2)).CountedValue.Should().Be(0m);
        result.PerDay.First(d => d.Date == new DateOnly(2026, 3, 3)).CountedValue.Should().Be(1m);
    }

    private static VacationRequest CreateRequest(DateOnly start, DateOnly end, bool startHalf = false, bool endHalf = false, string state = "NW", decimal annual = 30m, decimal approved = 0m, bool studentActive = false, Dictionary<DayOfWeek, VocationalSchoolDayType>? vocational = null, HashSet<DayOfWeek>? workdays = null, HashSet<DateOnly>? azaDates = null)
    {
        vocational ??= Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => VocationalSchoolDayType.None);
        workdays ??= new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
        azaDates ??= new HashSet<DateOnly>();
        return new VacationRequest(start, end, startHalf, endHalf, workdays, new StudentParameters(studentActive, state, vocational), state, annual, approved, start.Year, azaDates);
    }

    private sealed class NoHolidayProvider : IPublicHolidayProvider
    {
        public bool IsPublicHoliday(DateOnly date, string state) => false;
    }

    private sealed class NoSchoolHolidayProvider : ISchoolHolidayProvider
    {
        public bool IsSchoolHoliday(DateOnly date, string state) => false;
    }

    private sealed class StubPublicHolidayProvider : IPublicHolidayProvider
    {
        private readonly DateOnly _holiday;
        public StubPublicHolidayProvider(DateOnly holiday) => _holiday = holiday;
        public bool IsPublicHoliday(DateOnly date, string state) => date == _holiday;
    }

    private sealed class StubSchoolHolidayProvider : ISchoolHolidayProvider
    {
        private readonly DateOnly _holiday;
        public StubSchoolHolidayProvider(DateOnly holiday) => _holiday = holiday;
        public bool IsSchoolHoliday(DateOnly date, string state) => date == _holiday;
    }
}
