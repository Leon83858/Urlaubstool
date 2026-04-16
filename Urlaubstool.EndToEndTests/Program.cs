using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.Holidays;
using Urlaubstool.Infrastructure.Ledger;
using Urlaubstool.Infrastructure.Paths;
using Urlaubstool.Infrastructure.Pdf;
using Urlaubstool.Infrastructure.Settings;

class EndToEndTests
{
    static void Main()
    {
        Console.WriteLine("=== URLAUBSTOOL END-TO-END SCENARIO TESTS ===\n");
        
        var tests = new ScenarioTests();
        
        // Scenario A: Basic employee mode (no student params)
        RunScenario("A", "Basic Employee Mode", () => tests.ScenarioA_BasicMode());
        
        // Scenario B: Public holiday exclusion
        RunScenario("B", "Public Holiday Exclusion", () => tests.ScenarioB_PublicHoliday());
        
        // Scenario C: Student mode, full school day blocks
        RunScenario("C", "Student Full Day Block", () => tests.ScenarioC_StudentFullBlock());
        
        // Scenario D: Student mode, half school day caps
        RunScenario("D", "Student Half Day Cap", () => tests.ScenarioD_StudentHalfCap());
        
        // Scenario E: Half-day edge rule
        RunScenario("E", "Half-day Edge Rules", () => tests.ScenarioE_HalfDayEdges());
        
        // Scenario F: Year boundary error
        RunScenario("F", "Year Boundary Error", () => tests.ScenarioF_YearBoundary());
        
        // Scenario G: History workflow
        RunScenario("G", "History Workflow", () => tests.ScenarioG_HistoryWorkflow());
        
        Console.WriteLine("\n=== END-TO-END TESTS COMPLETE ===");
    }
    
    static void RunScenario(string id, string name, Func<bool> testFunc)
    {
        Console.WriteLine($"Scenario {id}: {name}");
        int passCount = 0;
        for (int run = 1; run <= 5; run++)
        {
            try
            {
                bool passed = testFunc();
                if (passed)
                {
                    Console.WriteLine($"  Run {run}: ✓ PASS");
                    passCount++;
                }
                else
                {
                    Console.WriteLine($"  Run {run}: ✗ FAIL");
                    return; // Stop and show error
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Run {run}: ✗ EXCEPTION: {ex.Message}");
                return;
            }
        }
        Console.WriteLine($"Scenario {id}: {passCount}/5 PASSED ✓\n");
    }
}

internal class ScenarioTests
{
    private readonly IPublicHolidayProvider _publicHolidayProvider = new PublicHolidayProvider();
    private readonly ISchoolHolidayProvider _schoolHolidayProvider = new SchoolHolidayProvider();
    private readonly VacationCalculator _calculator;
    private readonly PathService _pathService;
    private readonly SettingsService _settingsService;
    private readonly LedgerService _ledgerService;
    private readonly PdfExportService _pdfService;
    
    public ScenarioTests()
    {
        _calculator = new VacationCalculator(_publicHolidayProvider, _schoolHolidayProvider);
        var tmpDir = Path.Combine(Path.GetTempPath(), $"urlaubstool_e2e_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        _pathService = new TestPathService(tmpDir);
        _settingsService = new SettingsService(_pathService);
        _ledgerService = new LedgerService(_pathService);
        _pdfService = new PdfExportService(_pathService);
    }
    
    public bool ScenarioA_BasicMode()
    {
        // Mo-Fr, 30 days annual, normal week 13-17 Jan 2025 = 5 days
        var settings = new AppSettings
        {
            Name = "Test User", Abteilung = "IT", Klasse = "FI1",
            Jahresurlaub = 30m,
            Workdays = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
            StudentActive = false,
            Bundesland = "NW",
            VocationalSchool = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => VocationalSchoolDayType.None)
        };
        
        var request = new VacationRequest(
            new DateOnly(2025, 1, 13), new DateOnly(2025, 1, 17),
            false, false,
            settings.Workdays,
            new StudentParameters(false, "NW", settings.VocationalSchool),
            "NW", 30m, 0m, 2025
        );
        
        var result = _calculator.Calculate(request);
        
        // Verify: 5 days, no errors
        if (result.HasErrors || Math.Abs(result.TotalDays - 5m) > 0.01m)
            return false;
        
        // Verify PDF export
        var entitlement = EntitlementSnapshot.From(30m, 0m);
        var pdfPath = _pdfService.Export(settings, request, result, entitlement);
        return File.Exists(pdfPath);
    }
    
    public bool ScenarioB_PublicHoliday()
    {
        // 03.10 is Deutscher Einheitstag (nationwide) - should count as 0
        var settings = new AppSettings
        {
            Name = "Test", Abteilung = "IT", Klasse = "FI1",
            Jahresurlaub = 30m,
            Workdays = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
            StudentActive = false,
            Bundesland = "NW",
            VocationalSchool = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => VocationalSchoolDayType.None)
        };
        
        // 03.10.2025 is a Thursday
        var request = new VacationRequest(
            new DateOnly(2025, 10, 3), new DateOnly(2025, 10, 3),
            false, false,
            settings.Workdays,
            new StudentParameters(false, "NW", settings.VocationalSchool),
            "NW", 30m, 0m, 2025
        );
        
        var result = _calculator.Calculate(request);
        
        // Verify: 0 days (holiday), no errors
        return !result.HasErrors && Math.Abs(result.TotalDays) < 0.01m;
    }
    
    public bool ScenarioC_StudentFullBlock()
    {
        // Monday = Full day block, try to book a Monday -> ERROR
        var vocSchool = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => VocationalSchoolDayType.None);
        vocSchool[DayOfWeek.Monday] = VocationalSchoolDayType.Full;
        
        var settings = new AppSettings
        {
            Name = "Student", Abteilung = "Klasse", Klasse = "FI1",
            Jahresurlaub = 30m,
            Workdays = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
            StudentActive = true,
            Bundesland = "NW",
            VocationalSchool = vocSchool
        };
        
        // Book Monday 13 Jan 2025
        var request = new VacationRequest(
            new DateOnly(2025, 1, 13), new DateOnly(2025, 1, 13),
            false, false,
            settings.Workdays,
            new StudentParameters(true, "NW", vocSchool),
            "NW", 30m, 0m, 2025
        );
        
        var result = _calculator.Calculate(request);
        
        // Verify: ERROR (should contain "Berufsschule" and "ganztägig")
        return result.HasErrors && result.Errors.Any(e => e.Contains("Berufsschule"));
    }
    
    public bool ScenarioD_StudentHalfCap()
    {
        // Monday = Half day, book Monday -> counts as 0.5 only
        var vocSchool = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => VocationalSchoolDayType.None);
        vocSchool[DayOfWeek.Monday] = VocationalSchoolDayType.Half;
        
        var settings = new AppSettings
        {
            Name = "Student", Abteilung = "Klasse", Klasse = "FI1",
            Jahresurlaub = 30m,
            Workdays = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
            StudentActive = true,
            Bundesland = "NW",
            VocationalSchool = vocSchool
        };
        
        var request = new VacationRequest(
            new DateOnly(2025, 1, 13), new DateOnly(2025, 1, 13),
            false, false,
            settings.Workdays,
            new StudentParameters(true, "NW", vocSchool),
            "NW", 30m, 0m, 2025
        );
        
        var result = _calculator.Calculate(request);
        
        // Verify: 0.5 days, no errors
        return !result.HasErrors && Math.Abs(result.TotalDays - 0.5m) < 0.01m;
    }
    
    public bool ScenarioE_HalfDayEdges()
    {
        // 20-22 Jan = Mon-Wed: half-day on Monday = 2.5 days total
        var settings = new AppSettings
        {
            Name = "Test", Abteilung = "IT", Klasse = "FI1",
            Jahresurlaub = 30m,
            Workdays = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
            StudentActive = false,
            Bundesland = "NW",
            VocationalSchool = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => VocationalSchoolDayType.None)
        };
        
        var request = new VacationRequest(
            new DateOnly(2025, 1, 20), new DateOnly(2025, 1, 22),
            true, false,
            settings.Workdays,
            new StudentParameters(false, "NW", settings.VocationalSchool),
            "NW", 30m, 0m, 2025
        );
        
        var result = _calculator.Calculate(request);
        
        // Verify: 2.5 days, no errors
        return !result.HasErrors && Math.Abs(result.TotalDays - 2.5m) < 0.01m;
    }
    
    public bool ScenarioF_YearBoundary()
    {
        // 31.12.2025 to 02.01.2026 -> ERROR
        var settings = new AppSettings
        {
            Name = "Test", Abteilung = "IT", Klasse = "FI1",
            Jahresurlaub = 30m,
            Workdays = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
            StudentActive = false,
            Bundesland = "NW",
            VocationalSchool = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => VocationalSchoolDayType.None)
        };
        
        var request = new VacationRequest(
            new DateOnly(2025, 12, 31), new DateOnly(2026, 1, 2),
            false, false,
            settings.Workdays,
            new StudentParameters(false, "NW", settings.VocationalSchool),
            "NW", 30m, 0m, 2025
        );
        
        var result = _calculator.Calculate(request);
        
        // Verify: ERROR about year boundary
        return result.HasErrors && result.Errors.Any(e => e.Contains("Kalenderjahr"));
    }
    
    public bool ScenarioG_HistoryWorkflow()
    {
        // Create, approve, check ledger
        var settings = new AppSettings
        {
            Name = "Test", Abteilung = "IT", Klasse = "FI1",
            Jahresurlaub = 30m,
            Workdays = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
            StudentActive = false,
            Bundesland = "NW",
            VocationalSchool = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => VocationalSchoolDayType.None)
        };
        
        var request = new VacationRequest(
            new DateOnly(2025, 1, 13), new DateOnly(2025, 1, 17),
            false, false,
            settings.Workdays,
            new StudentParameters(false, "NW", settings.VocationalSchool),
            "NW", 30m, 0m, 2025
        );
        
        var result = _calculator.Calculate(request);
        if (result.HasErrors) return false;
        
        // Save ledger entry
        var entry = new LedgerEntry(
            LedgerService.SchemaVersion, 2025, Guid.NewGuid(),
            DateTimeOffset.Now,
            request.StartDate, request.EndDate,
            false, false, 5m,
            VacationRequestStatus.Approved,
            null, null, null
        );
        
        _ledgerService.Save(new[] { entry });
        
        var loaded = _ledgerService.Load();
        
        // Verify: entry exists, approved total = 5m
        return loaded.Entries.Count > 0 && Math.Abs(loaded.GetApprovedTotal(2025) - 5m) < 0.01m;
    }
}

internal class TestPathService : PathService
{
    private readonly string _root;
    public TestPathService(string root) => _root = root;
    public override string GetAppDataDirectory() => _root;
    public override string GetExportDirectory() => Path.Combine(_root, "exports");
}
