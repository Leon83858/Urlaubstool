using FluentAssertions;
using Xunit;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.Holidays;
using Urlaubstool.Infrastructure.Ledger;
using Urlaubstool.Infrastructure.Paths;
using Urlaubstool.Infrastructure.Pdf;
using Urlaubstool.Infrastructure.Settings;

namespace Urlaubstool.DeskCheckTests;

/// <summary>
/// Desk-Check Szenarien A-G gemäß Anforderung 11.3
/// Jedes Szenario wird 5 mal ausgeführt um Stabilität zu gewährleisten
/// </summary>
public class DeskCheckScenarios
{
    private readonly VacationCalculator _calculator;
    private readonly SettingsService _settingsService;
    private readonly LedgerService _ledgerService;
    private readonly PdfExportService _pdfService;
    private readonly PathService _pathService;

    public DeskCheckScenarios()
    {
        _pathService = new PathService();
        var publicHolidayProvider = new PublicHolidayProvider();
        var schoolHolidayProvider = new SchoolHolidayProvider();
        
        _calculator = new VacationCalculator(publicHolidayProvider, schoolHolidayProvider);
        _settingsService = new SettingsService(_pathService);
        _ledgerService = new LedgerService(_pathService);
        _pdfService = new PdfExportService(_pathService);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ScenarioA_BasicEmployeeMode_NoStudentParameters(int iteration)
    {
        // SCENARIO A: Basic employee mode (no student parameters)
        // 1) Disable Schülerparameter
        // 2) Configure Workdays = Mo–Fr, Entitlement = 30
        // 3) Create a request for a normal Mon–Fri week (no holidays)
        // 4) Verify total days = 5
        // 5) Export PDF succeeds
        // 6) Entry appears in history correctly

        // Setup: Employee mit Mo-Fr Arbeitstagen, kein Schüler
        var workdays = new HashSet<DayOfWeek> 
        { 
            DayOfWeek.Monday, 
            DayOfWeek.Tuesday, 
            DayOfWeek.Wednesday, 
            DayOfWeek.Thursday, 
            DayOfWeek.Friday 
        };
        
        var studentParams = new StudentParameters(
            Active: false,
            State: null,
            VocationalSchoolDays: new Dictionary<DayOfWeek, VocationalSchoolDayType>()
        );

        // Eine normale Woche: 20.01.2026 (Dienstag) bis 23.01.2026 (Freitag) = 4 Tage
        // Nutze 19.01 (Montag) bis 23.01 (Freitag) für volle 5 Tage
        var request = new VacationRequest(
            StartDate: new DateOnly(2026, 1, 19),  // Montag
            EndDate: new DateOnly(2026, 1, 23),    // Freitag
            StartHalfDay: false,
            EndHalfDay: false,
            WorkdaysOfWeek: workdays,
            Student: studentParams,
            State: null,
            AnnualEntitlement: 30m,
            AlreadyApprovedThisYear: 0m,
            Year: 2026,
            AzaDates: new HashSet<DateOnly>()
        );

        // Berechnung durchführen
        var result = _calculator.Calculate(request);

        // Verifikation
        result.HasErrors.Should().BeFalse($"Iteration {iteration}: Keine Fehler erwartet");
        result.TotalDays.Should().Be(5m, $"Iteration {iteration}: Mo-Fr sollte 5 Tage sein");
        
        // Alle Tage sollten Arbeitstage sein
        result.PerDay.Should().HaveCount(5, $"Iteration {iteration}: 5 Tage erwartet");
        result.PerDay.Should().OnlyContain(d => d.IsWorkday, $"Iteration {iteration}: Alle sollten Arbeitstage sein");
        result.PerDay.Should().OnlyContain(d => d.CountedValue == 1m, $"Iteration {iteration}: Jeder Tag sollte 1.0 zählen");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ScenarioB_PublicHolidayExclusion(int iteration)
    {
        // SCENARIO B: Public holiday exclusion
        // 1) Use a known nationwide holiday (e.g., 03.10) inside a range
        // 2) Verify holiday is counted as 0
        // 3) Export PDF succeeds and shows correct totals

        var workdays = new HashSet<DayOfWeek> 
        { 
            DayOfWeek.Monday, 
            DayOfWeek.Tuesday, 
            DayOfWeek.Wednesday, 
            DayOfWeek.Thursday, 
            DayOfWeek.Friday 
        };
        
        var studentParams = new StudentParameters(
            Active: false,
            State: null,
            VocationalSchoolDays: new Dictionary<DayOfWeek, VocationalSchoolDayType>()
        );

        // Tag der Deutschen Einheit: 03.10.2026 (Samstag - aber egal, zählt 0)
        // Nutze 01.10 (Donnerstag) bis 06.10 (Dienstag) 2026
        // Das sind: Do 01.10, Fr 02.10, Sa 03.10 (Feiertag), So 04.10, Mo 05.10, Di 06.10
        // Arbeitstage: Do, Fr, Mo, Di = 4 Tage (Sa/So sind keine Arbeitstage)
        var request = new VacationRequest(
            StartDate: new DateOnly(2026, 10, 1),   // Donnerstag
            EndDate: new DateOnly(2026, 10, 6),     // Dienstag
            StartHalfDay: false,
            EndHalfDay: false,
            WorkdaysOfWeek: workdays,
            Student: studentParams,
            State: "BW",  // Bundesweit gilt 03.10
            AnnualEntitlement: 30m,
            AlreadyApprovedThisYear: 0m,
            Year: 2026,
            AzaDates: new HashSet<DateOnly>()
        );

        var result = _calculator.Calculate(request);

        result.HasErrors.Should().BeFalse($"Iteration {iteration}: Keine Fehler erwartet");
        
        // Prüfe dass 03.10 als Feiertag markiert ist
        var holiday = result.PerDay.FirstOrDefault(d => d.Date == new DateOnly(2026, 10, 3));
        holiday.Should().NotBeNull($"Iteration {iteration}: 03.10 sollte in der Liste sein");
        holiday!.IsPublicHoliday.Should().BeTrue($"Iteration {iteration}: 03.10 ist Tag der Deutschen Einheit");
        
        // Gesamt sollte 4 sein (Do, Fr, Mo, Di ohne Feiertag)
        result.TotalDays.Should().Be(4m, $"Iteration {iteration}: 4 Arbeitstage ohne Feiertag");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ScenarioC_StudentMode_FullSchoolDayBlocks(int iteration)
    {
        // SCENARIO C: Student mode, full school day blocks
        // 1) Enable Schülerparameter and set at least one weekday to "Voll"
        // 2) Choose a range that includes that weekday outside school holidays
        // 3) Verify the calculator returns a hard error and Export is blocked
        // 4) Verify the error message is German and clear

        var workdays = new HashSet<DayOfWeek> 
        { 
            DayOfWeek.Monday, 
            DayOfWeek.Tuesday, 
            DayOfWeek.Wednesday, 
            DayOfWeek.Thursday, 
            DayOfWeek.Friday 
        };
        
        var vocationalDays = new Dictionary<DayOfWeek, VocationalSchoolDayType>
        {
            { DayOfWeek.Monday, VocationalSchoolDayType.Full }  // Montag = Voller Schultag
        };
        
        var studentParams = new StudentParameters(
            Active: true,
            State: "BW",
            VocationalSchoolDays: vocationalDays
        );

        // Zeitraum mit Montag außerhalb der Schulferien: 19.01-23.01.2026
        var request = new VacationRequest(
            StartDate: new DateOnly(2026, 1, 19),  // Montag
            EndDate: new DateOnly(2026, 1, 23),    // Freitag
            StartHalfDay: false,
            EndHalfDay: false,
            WorkdaysOfWeek: workdays,
            Student: studentParams,
            State: "BW",
            AnnualEntitlement: 30m,
            AlreadyApprovedThisYear: 0m,
            Year: 2026,
            AzaDates: new HashSet<DateOnly>()
        );

        var result = _calculator.Calculate(request);

        // MUSS Fehler haben wegen vollem Berufsschultag
        result.HasErrors.Should().BeTrue($"Iteration {iteration}: Fehler erwartet bei vollem Berufsschultag");
        result.Errors.Should().NotBeEmpty($"Iteration {iteration}: Fehlerliste sollte nicht leer sein");
        result.Errors.Should().Contain(e => e.Contains("ganztägigen Schultagen"), 
            $"Iteration {iteration}: Fehlermeldung sollte 'An ganztägigen Schultagen kann kein Urlaub genommen werden' enthalten");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ScenarioD_StudentMode_HalfSchoolDayCaps(int iteration)
    {
        // SCENARIO D: Student mode, half school day caps
        // 1) Set at least one weekday to "Halb"
        // 2) Choose a range that includes that weekday outside school holidays
        // 3) Verify that day counts max 0.5, and totals reflect it

        var workdays = new HashSet<DayOfWeek> 
        { 
            DayOfWeek.Monday, 
            DayOfWeek.Tuesday, 
            DayOfWeek.Wednesday, 
            DayOfWeek.Thursday, 
            DayOfWeek.Friday 
        };
        
        var vocationalDays = new Dictionary<DayOfWeek, VocationalSchoolDayType>
        {
            { DayOfWeek.Wednesday, VocationalSchoolDayType.Half }  // Mittwoch = Halber Schultag
        };
        
        var studentParams = new StudentParameters(
            Active: true,
            State: "BW",
            VocationalSchoolDays: vocationalDays
        );

        // Zeitraum mit Mittwoch: 19.01-23.01.2026 (Mo-Fr)
        var request = new VacationRequest(
            StartDate: new DateOnly(2026, 1, 19),  // Montag
            EndDate: new DateOnly(2026, 1, 23),    // Freitag
            StartHalfDay: false,
            EndHalfDay: false,
            WorkdaysOfWeek: workdays,
            Student: studentParams,
            State: "BW",
            AnnualEntitlement: 30m,
            AlreadyApprovedThisYear: 0m,
            Year: 2026,
            AzaDates: new HashSet<DateOnly>()
        );

        var result = _calculator.Calculate(request);

        result.HasErrors.Should().BeFalse($"Iteration {iteration}: Keine Fehler erwartet bei halbem Berufsschultag");
        
        // Mittwoch sollte max 0.5 zählen
        var wednesday = result.PerDay.First(d => d.DayOfWeek == DayOfWeek.Wednesday);
        wednesday.CountedValue.Should().Be(0.5m, $"Iteration {iteration}: Mittwoch (halber Schultag) sollte 0.5 zählen");
        wednesday.VocationalSchool.Should().Be(VocationalSchoolDayType.Half);
        
        // Gesamt: Mo(1) + Di(1) + Mi(0.5) + Do(1) + Fr(1) = 4.5
        result.TotalDays.Should().Be(4.5m, $"Iteration {iteration}: 4.5 Tage mit halbem Schultag erwartet");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ScenarioE_HalfDayEdgeRule(int iteration)
    {
        // SCENARIO E: Half-day edge rule
        // 1) Create a multi-day range
        // 2) Apply StartHalfDay and/or EndHalfDay
        // 3) Verify only edges are capped and internal days are not affected

        var workdays = new HashSet<DayOfWeek> 
        { 
            DayOfWeek.Monday, 
            DayOfWeek.Tuesday, 
            DayOfWeek.Wednesday, 
            DayOfWeek.Thursday, 
            DayOfWeek.Friday 
        };
        
        var studentParams = new StudentParameters(
            Active: false,
            State: null,
            VocationalSchoolDays: new Dictionary<DayOfWeek, VocationalSchoolDayType>()
        );

        // Mo-Fr mit Start und End halbtags
        var request = new VacationRequest(
            StartDate: new DateOnly(2026, 1, 19),  // Montag
            EndDate: new DateOnly(2026, 1, 23),    // Freitag
            StartHalfDay: true,    // Montag halbtags
            EndHalfDay: true,      // Freitag halbtags
            WorkdaysOfWeek: workdays,
            Student: studentParams,
            State: null,
            AnnualEntitlement: 30m,
            AlreadyApprovedThisYear: 0m,
            Year: 2026,
            AzaDates: new HashSet<DateOnly>()
        );

        var result = _calculator.Calculate(request);

        result.HasErrors.Should().BeFalse($"Iteration {iteration}: Keine Fehler erwartet");
        
        // Montag sollte 0.5 sein (Start halbtags)
        var monday = result.PerDay.First(d => d.DayOfWeek == DayOfWeek.Monday);
        monday.CountedValue.Should().Be(0.5m, $"Iteration {iteration}: Montag (Start halbtags) sollte 0.5 sein");
        
        // Dienstag-Donnerstag sollten je 1.0 sein
        var tuesday = result.PerDay.First(d => d.DayOfWeek == DayOfWeek.Tuesday);
        tuesday.CountedValue.Should().Be(1.0m, $"Iteration {iteration}: Dienstag sollte 1.0 sein");
        
        var wednesday = result.PerDay.First(d => d.DayOfWeek == DayOfWeek.Wednesday);
        wednesday.CountedValue.Should().Be(1.0m, $"Iteration {iteration}: Mittwoch sollte 1.0 sein");
        
        var thursday = result.PerDay.First(d => d.DayOfWeek == DayOfWeek.Thursday);
        thursday.CountedValue.Should().Be(1.0m, $"Iteration {iteration}: Donnerstag sollte 1.0 sein");
        
        // Freitag sollte 0.5 sein (End halbtags)
        var friday = result.PerDay.First(d => d.DayOfWeek == DayOfWeek.Friday);
        friday.CountedValue.Should().Be(0.5m, $"Iteration {iteration}: Freitag (End halbtags) sollte 0.5 sein");
        
        // Gesamt: 0.5 + 1 + 1 + 1 + 0.5 = 4.0
        result.TotalDays.Should().Be(4.0m, $"Iteration {iteration}: 4.0 Tage mit beidseitig halbtags erwartet");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ScenarioF_YearBoundaryError(int iteration)
    {
        // SCENARIO F: Year boundary error
        // 1) Create a range that crosses 31.12 -> 01.01
        // 2) Verify hard error and Export blocked

        var workdays = new HashSet<DayOfWeek> 
        { 
            DayOfWeek.Monday, 
            DayOfWeek.Tuesday, 
            DayOfWeek.Wednesday, 
            DayOfWeek.Thursday, 
            DayOfWeek.Friday 
        };
        
        var studentParams = new StudentParameters(
            Active: false,
            State: null,
            VocationalSchoolDays: new Dictionary<DayOfWeek, VocationalSchoolDayType>()
        );

        // 30.12.2026 (Mittwoch) bis 05.01.2027 (Dienstag) - überschreitet Jahresgrenze
        var request = new VacationRequest(
            StartDate: new DateOnly(2026, 12, 30),
            EndDate: new DateOnly(2027, 1, 5),
            StartHalfDay: false,
            EndHalfDay: false,
            WorkdaysOfWeek: workdays,
            Student: studentParams,
            State: null,
            AnnualEntitlement: 30m,
            AlreadyApprovedThisYear: 0m,
            Year: 2026,
            AzaDates: new HashSet<DateOnly>()
        );

        var result = _calculator.Calculate(request);

        // MUSS Fehler haben wegen Jahresüberschreitung
        result.HasErrors.Should().BeTrue($"Iteration {iteration}: Fehler erwartet bei Jahresüberschreitung");
        result.Errors.Should().NotBeEmpty($"Iteration {iteration}: Fehlerliste sollte nicht leer sein");
        result.Errors.Should().Contain(e => e.Contains("Jahr") || e.Contains("Grenze") || e.Contains("überschreitet"), 
            $"Iteration {iteration}: Fehlermeldung sollte auf Jahresgrenze hinweisen");
    }

    [Fact]
    public async System.Threading.Tasks.Task ScenarioG_HistoryWorkflow_AllIterations()
    {
        // SCENARIO G: History workflow
        // Da dies Persistenz-Tests sind, führe ich sie sequenziell durch
        // Jede Iteration testet: Create/Export -> Approve -> Verify reduction

        for (int iteration = 1; iteration <= 5; iteration++)
        {
            // Setup temporäres Verzeichnis für jeden Durchlauf
            var tempDir = Path.Combine(Path.GetTempPath(), $"urlaubstool_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            
            try
            {
                var tempPathService = new TestPathService(tempDir);
                var tempLedgerService = new LedgerService(tempPathService);
                var tempSettingsService = new SettingsService(tempPathService);
                var tempPdfService = new PdfExportService(tempPathService);

                // Settings erstellen
                var settings = new AppSettings
                {
                    Version = 1,
                    Name = "Test User",
                    Abteilung = "IT",
                    Klasse = string.Empty,
                    Jahresurlaub = 30m,
                    Workdays = new HashSet<DayOfWeek> 
                    { 
                        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, 
                        DayOfWeek.Thursday, DayOfWeek.Friday 
                    },
                    StudentActive = false,
                    Bundesland = null,
                    VocationalSchool = new Dictionary<DayOfWeek, VocationalSchoolDayType>()
                };
                
                // Await the async save operation instead of blocking with .Wait()
                await tempSettingsService.SaveAsync(settings);

                // Request erstellen und berechnen
                var workdays = new HashSet<DayOfWeek> 
                { 
                    DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, 
                    DayOfWeek.Thursday, DayOfWeek.Friday 
                };
                
                var studentParams = new StudentParameters(
                    Active: false,
                    State: null,
                    VocationalSchoolDays: new Dictionary<DayOfWeek, VocationalSchoolDayType>()
                );

                var request = new VacationRequest(
                    StartDate: new DateOnly(2026, 1, 19),
                    EndDate: new DateOnly(2026, 1, 23),
                    StartHalfDay: false,
                    EndHalfDay: false,
                    WorkdaysOfWeek: workdays,
                    Student: studentParams,
                    State: null,
                    AnnualEntitlement: 30m,
                    AlreadyApprovedThisYear: 0m,
                    Year: 2026,
            AzaDates: new HashSet<DateOnly>()
                );

                var result = _calculator.Calculate(request);
                result.HasErrors.Should().BeFalse($"Iteration {iteration}: Berechnung sollte fehlerfrei sein");

                // PDF exportieren und Ledger-Eintrag erstellen
                var approvedDays = 0m;
                var entitlement = new EntitlementSnapshot(
                    AnnualEntitlement: settings.Jahresurlaub,
                    AlreadyApproved: approvedDays,
                    Remaining: settings.Jahresurlaub - approvedDays
                );
                
                var pdfPath = tempPdfService.Export(settings, request, result, entitlement);
                
                File.Exists(pdfPath).Should().BeTrue($"Iteration {iteration}: PDF sollte existieren");

                // Ledger-Eintrag erstellen
                var entry = new LedgerEntry(
                    SchemaVersion: 1,
                    Year: 2026,
                    RequestId: Guid.NewGuid(),
                    CreatedAt: DateTimeOffset.Now,
                    StartDate: request.StartDate,
                    EndDate: request.EndDate,
                    StartHalfDay: request.StartHalfDay,
                    EndHalfDay: request.EndHalfDay,
                    DaysRequested: result.TotalDays,
                    Status: VacationRequestStatus.Exported,
                    RejectionReason: null,
                    ArchivedAt: null,
                    PdfPath: pdfPath
                );

                var ledger = tempLedgerService.Load();
                var entries = ledger.Entries.ToList();
                entries.Add(entry);
                tempLedgerService.Save(entries);

                // Eintrag genehmigen
                var approvedEntry = entry with { Status = VacationRequestStatus.Approved };
                entries[entries.Count - 1] = approvedEntry;
                tempLedgerService.Save(entries);

                // Verifikation: Ledger neu laden und prüfen
                var reloadedLedger = tempLedgerService.Load();
                reloadedLedger.Entries.Should().HaveCount(1, $"Iteration {iteration}: Ein Eintrag erwartet");
                
                var savedEntry = reloadedLedger.Entries.First();
                savedEntry.Status.Should().Be(VacationRequestStatus.Approved, $"Iteration {iteration}: Status sollte Approved sein");
                savedEntry.DaysRequested.Should().Be(5m, $"Iteration {iteration}: 5 Tage sollten gespeichert sein");

                // Resturlaub berechnen
                var approvedTotal = reloadedLedger.GetApprovedTotal(2026);
                approvedTotal.Should().Be(5m, $"Iteration {iteration}: 5 genehmigte Tage erwartet");
                
                var remaining = 30m - approvedTotal;
                remaining.Should().Be(25m, $"Iteration {iteration}: 25 Tage Resturlaub erwartet");
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }
    }
}

/// <summary>
/// Test-PathService der ein temporäres Verzeichnis nutzt
/// </summary>
public class TestPathService : PathService
{
    private readonly string _testDir;

    public TestPathService(string testDir)
    {
        _testDir = testDir;
    }

    public override string GetAppDataDirectory()
    {
        return _testDir;
    }

    public override string GetExportDirectory()
    {
        var folder = Path.Combine(_testDir, "PDFs");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
