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

namespace Urlaubstool.EndToEndTests;

public class DeskCheckSimulation
{
    private readonly VacationCalculator _calculator;
    private readonly PdfExportService _pdfService;
    private readonly LedgerService _ledgerService;
    private readonly SettingsService _settingsService;
    private readonly string _tmpDir;

    public DeskCheckSimulation()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"urlaubstool_deskcheck_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);

        var publicHolidayProvider = new PublicHolidayProvider();
        var schoolHolidayProvider = new SchoolHolidayProvider();
        _calculator = new VacationCalculator(publicHolidayProvider, schoolHolidayProvider);
        var pathService = new TestPathService(_tmpDir);
        _settingsService = new SettingsService(pathService);
        _ledgerService = new LedgerService(pathService);
        _pdfService = new PdfExportService(pathService);
    }

    public bool RunAllScenarios()
    {
        Console.WriteLine("=== URLAUBSTOOL DESK-CHECK SIMULATION ===");
        Console.WriteLine("Each scenario must pass 5 consecutive times.\n");

        bool allPassed = true;

        allPassed &= RunScenarioA();
        allPassed &= RunScenarioB();
        allPassed &= RunScenarioC();
        allPassed &= RunScenarioD();
        allPassed &= RunScenarioE();
        allPassed &= RunScenarioF();
        allPassed &= RunScenarioG();

        Console.WriteLine($"\n=== FINAL RESULT: {(allPassed ? "ALL SCENARIOS PASSED" : "SOME SCENARIOS FAILED")} ===");
        Console.WriteLine($"Test directory: {_tmpDir}");
        
        return allPassed;
    }

    private bool RunScenarioA()
    {
        Console.WriteLine("\n=== Scenario A: Basic employee mode (no student parameters) ===");
        
        var settings = CreateBasicEmployeeSettings();
        int passCount = 0;

        for (int run = 1; run <= 5; run++)
        {
            Console.Write($"Run {run}/5... ");
            
            // Create a request for Mon-Fri week (Jan 13-17, 2025)
            var request = new VacationRequest(
                new DateOnly(2025, 1, 13), // Monday
                new DateOnly(2025, 1, 17), // Friday
                false,
                false,
                settings.Workdays,
                new StudentParameters(false, "NW", new Dictionary<DayOfWeek, VocationalSchoolDayType>()),
                "NW",
                settings.Jahresurlaub,
                0m,
                2025
            );

            var result = _calculator.Calculate(request);

            // Verify: total days = 5, no errors
            if (result.HasErrors)
            {
                Console.WriteLine($"FAIL: {string.Join(", ", result.Errors)}");
                return false;
            }

            if (result.TotalDays != 5m)
            {
                Console.WriteLine($"FAIL: Expected 5 days, got {result.TotalDays}");
                return false;
            }

            // Export PDF
            try
            {
                var entitlement = EntitlementSnapshot.From(settings.Jahresurlaub, 0m);
                var pdfPath = _pdfService.Export(settings, request, result, entitlement);
                
                if (!File.Exists(pdfPath))
                {
                    Console.WriteLine("FAIL: PDF export failed");
                    return false;
                }

                // Verify PDF has content
                var fileInfo = new FileInfo(pdfPath);
                if (fileInfo.Length < 1000)
                {
                    Console.WriteLine($"FAIL: PDF too small ({fileInfo.Length} bytes)");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL: PDF export exception: {ex.Message}");
                return false;
            }

            // Verify in ledger (simulate history entry)
            var ledgerEntry = new LedgerEntry(
                LedgerService.SchemaVersion,
                2025,
                Guid.NewGuid(),
                DateTimeOffset.Now,
                request.StartDate,
                request.EndDate,
                request.StartHalfDay,
                request.EndHalfDay,
                result.TotalDays,
                VacationRequestStatus.Exported,
                null,
                null,
                null
            );

            _ledgerService.Save(new[] { ledgerEntry });
            var loaded = _ledgerService.Load();
            
            if (loaded.Entries.Count == 0)
            {
                Console.WriteLine("FAIL: Ledger entry not saved");
                return false;
            }

            passCount++;
            Console.WriteLine("PASS");
        }

        Console.WriteLine($"Scenario A: {passCount}/5 passes - SUCCESS");
        return true;
    }

    private bool RunScenarioB()
    {
        Console.WriteLine("\n=== Scenario B: Public holiday exclusion ===");
        
        var settings = CreateBasicEmployeeSettings();
        int passCount = 0;

        for (int run = 1; run <= 5; run++)
        {
            Console.Write($"Run {run}/5... ");
            
            // Request Oct 1-3, 2025 (includes Tag der Deutschen Einheit on Oct 3)
            var request = new VacationRequest(
                new DateOnly(2025, 10, 1), // Wednesday
                new DateOnly(2025, 10, 3), // Friday (holiday)
                false,
                false,
                settings.Workdays,
                new StudentParameters(false, "NW", new Dictionary<DayOfWeek, VocationalSchoolDayType>()),
                "NW",
                settings.Jahresurlaub,
                0m,
                2025
            );

            var result = _calculator.Calculate(request);

            // Verify: no errors, holiday counted as 0
            if (result.HasErrors)
            {
                Console.WriteLine($"FAIL: {string.Join(", ", result.Errors)}");
                return false;
            }

            // Oct 1 (Wed) = 1, Oct 2 (Thu) = 1, Oct 3 (Fri, holiday) = 0 → total = 2
            if (result.TotalDays != 2m)
            {
                Console.WriteLine($"FAIL: Expected 2 days (holiday excluded), got {result.TotalDays}");
                return false;
            }

            // Export PDF
            try
            {
                var entitlement = EntitlementSnapshot.From(settings.Jahresurlaub, 0m);
                var pdfPath = _pdfService.Export(settings, request, result, entitlement);
                
                if (!File.Exists(pdfPath))
                {
                    Console.WriteLine("FAIL: PDF export failed");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL: PDF export exception: {ex.Message}");
                return false;
            }

            passCount++;
            Console.WriteLine("PASS");
        }

        Console.WriteLine($"Scenario B: {passCount}/5 passes - SUCCESS");
        return true;
    }

    private bool RunScenarioC()
    {
        Console.WriteLine("\n=== Scenario C: Student mode, full school day blocks ===");
        
        var settings = new AppSettings
        {
            Name = "Test User",
            Abteilung = "IT",
            Klasse = "FI1",
            Jahresurlaub = 30m,
            Workdays = new HashSet<DayOfWeek> 
            { 
                DayOfWeek.Monday, 
                DayOfWeek.Tuesday, 
                DayOfWeek.Wednesday, 
                DayOfWeek.Thursday, 
                DayOfWeek.Friday 
            },
            StudentActive = true,
            Bundesland = "NW",
            VocationalSchool = new Dictionary<DayOfWeek, VocationalSchoolDayType>
            {
                { DayOfWeek.Monday, VocationalSchoolDayType.Full },
                { DayOfWeek.Tuesday, VocationalSchoolDayType.None },
                { DayOfWeek.Wednesday, VocationalSchoolDayType.None },
                { DayOfWeek.Thursday, VocationalSchoolDayType.None },
                { DayOfWeek.Friday, VocationalSchoolDayType.None },
                { DayOfWeek.Saturday, VocationalSchoolDayType.None },
                { DayOfWeek.Sunday, VocationalSchoolDayType.None }
            }
        };

        int passCount = 0;

        for (int run = 1; run <= 5; run++)
        {
            Console.Write($"Run {run}/5... ");
            
            // Request Jan 13-17, 2025 (includes Monday with Full school day, outside school holidays)
            var request = new VacationRequest(
                new DateOnly(2025, 1, 13), // Monday (Full school day)
                new DateOnly(2025, 1, 17), // Friday
                false,
                false,
                settings.Workdays,
                new StudentParameters(true, "NW", settings.VocationalSchool),
                "NW",
                settings.Jahresurlaub,
                0m,
                2025
            );

            var result = _calculator.Calculate(request);

            // Verify: hard error, export blocked
            if (!result.HasErrors)
            {
                Console.WriteLine("FAIL: Expected hard error for full school day");
                return false;
            }

            // Verify error message is German and mentions school
            var errorMsg = string.Join(" ", result.Errors);
            if (!errorMsg.Contains("Schultag", StringComparison.OrdinalIgnoreCase) && 
                !errorMsg.Contains("Berufsschule", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"FAIL: Error message not clear or not German: {errorMsg}");
                return false;
            }

            // Verify PDF export is blocked (should throw or fail)
            try
            {
                var entitlement = EntitlementSnapshot.From(settings.Jahresurlaub, 0m);
                var pdfPath = _pdfService.Export(settings, request, result, entitlement);
                
                // If we get here, PDF export should have been blocked but wasn't
                Console.WriteLine("FAIL: PDF export should be blocked when errors exist");
                return false;
            }
            catch (InvalidOperationException)
            {
                // Expected: export blocked
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL: Unexpected exception type: {ex.GetType().Name}");
                return false;
            }

            passCount++;
            Console.WriteLine("PASS");
        }

        Console.WriteLine($"Scenario C: {passCount}/5 passes - SUCCESS");
        return true;
    }

    private bool RunScenarioD()
    {
        Console.WriteLine("\n=== Scenario D: Student mode, half school day caps ===");
        
        var settings = new AppSettings
        {
            Name = "Test User",
            Abteilung = "IT",
            Klasse = "FI1",
            Jahresurlaub = 30m,
            Workdays = new HashSet<DayOfWeek> 
            { 
                DayOfWeek.Monday, 
                DayOfWeek.Tuesday, 
                DayOfWeek.Wednesday, 
                DayOfWeek.Thursday, 
                DayOfWeek.Friday 
            },
            StudentActive = true,
            Bundesland = "NW",
            VocationalSchool = new Dictionary<DayOfWeek, VocationalSchoolDayType>
            {
                { DayOfWeek.Monday, VocationalSchoolDayType.None },
                { DayOfWeek.Tuesday, VocationalSchoolDayType.Half },
                { DayOfWeek.Wednesday, VocationalSchoolDayType.None },
                { DayOfWeek.Thursday, VocationalSchoolDayType.None },
                { DayOfWeek.Friday, VocationalSchoolDayType.None },
                { DayOfWeek.Saturday, VocationalSchoolDayType.None },
                { DayOfWeek.Sunday, VocationalSchoolDayType.None }
            }
        };

        int passCount = 0;

        for (int run = 1; run <= 5; run++)
        {
            Console.Write($"Run {run}/5... ");
            
            // Request Jan 13-17, 2025 (includes Tuesday with Half school day, outside school holidays)
            var request = new VacationRequest(
                new DateOnly(2025, 1, 13), // Monday
                new DateOnly(2025, 1, 17), // Friday
                false,
                false,
                settings.Workdays,
                new StudentParameters(true, "NW", settings.VocationalSchool),
                "NW",
                settings.Jahresurlaub,
                0m,
                2025
            );

            var result = _calculator.Calculate(request);

            // Verify: no errors
            if (result.HasErrors)
            {
                Console.WriteLine($"FAIL: {string.Join(", ", result.Errors)}");
                return false;
            }

            // Jan 13 (Mon) = 1, Jan 14 (Tue, half) = 0.5, Jan 15 (Wed) = 1, Jan 16 (Thu) = 1, Jan 17 (Fri) = 1 → total = 4.5
            if (result.TotalDays != 4.5m)
            {
                Console.WriteLine($"FAIL: Expected 4.5 days (Tuesday capped to 0.5), got {result.TotalDays}");
                return false;
            }

            passCount++;
            Console.WriteLine("PASS");
        }

        Console.WriteLine($"Scenario D: {passCount}/5 passes - SUCCESS");
        return true;
    }

    private bool RunScenarioE()
    {
        Console.WriteLine("\n=== Scenario E: Half-day edge rule ===");
        
        var settings = CreateBasicEmployeeSettings();
        int passCount = 0;

        for (int run = 1; run <= 5; run++)
        {
            Console.Write($"Run {run}/5... ");
            
            // Test 1: Multi-day with start half-day
            var request1 = new VacationRequest(
                new DateOnly(2025, 1, 13), // Monday
                new DateOnly(2025, 1, 15), // Wednesday
                true,  // StartHalfDay
                false,
                settings.Workdays,
                new StudentParameters(false, "NW", new Dictionary<DayOfWeek, VocationalSchoolDayType>()),
                "NW",
                settings.Jahresurlaub,
                0m,
                2025
            );

            var result1 = _calculator.Calculate(request1);

            if (result1.HasErrors)
            {
                Console.WriteLine($"FAIL (Test 1): {string.Join(", ", result1.Errors)}");
                return false;
            }

            // Jan 13 (Mon, half) = 0.5, Jan 14 (Tue) = 1, Jan 15 (Wed) = 1 → total = 2.5
            if (result1.TotalDays != 2.5m)
            {
                Console.WriteLine($"FAIL (Test 1): Expected 2.5 days, got {result1.TotalDays}");
                return false;
            }

            // Test 2: Multi-day with end half-day
            var request2 = new VacationRequest(
                new DateOnly(2025, 1, 13), // Monday
                new DateOnly(2025, 1, 15), // Wednesday
                false,
                true,  // EndHalfDay
                settings.Workdays,
                new StudentParameters(false, "NW", new Dictionary<DayOfWeek, VocationalSchoolDayType>()),
                "NW",
                settings.Jahresurlaub,
                0m,
                2025
            );

            var result2 = _calculator.Calculate(request2);

            if (result2.HasErrors)
            {
                Console.WriteLine($"FAIL (Test 2): {string.Join(", ", result2.Errors)}");
                return false;
            }

            // Jan 13 (Mon) = 1, Jan 14 (Tue) = 1, Jan 15 (Wed, half) = 0.5 → total = 2.5
            if (result2.TotalDays != 2.5m)
            {
                Console.WriteLine($"FAIL (Test 2): Expected 2.5 days, got {result2.TotalDays}");
                return false;
            }

            // Test 3: Invalid half-day on single day (both start and end half)
            var request3 = new VacationRequest(
                new DateOnly(2025, 1, 13), // Monday
                new DateOnly(2025, 1, 13), // Monday (same day)
                true,  // StartHalfDay
                true,  // EndHalfDay (invalid: both on same day)
                settings.Workdays,
                new StudentParameters(false, "NW", new Dictionary<DayOfWeek, VocationalSchoolDayType>()),
                "NW",
                settings.Jahresurlaub,
                0m,
                2025
            );

            var result3 = _calculator.Calculate(request3);

            // Should be error
            if (!result3.HasErrors)
            {
                Console.WriteLine("FAIL (Test 3): Expected error for invalid half-day configuration");
                return false;
            }

            passCount++;
            Console.WriteLine("PASS");
        }

        Console.WriteLine($"Scenario E: {passCount}/5 passes - SUCCESS");
        return true;
    }

    private bool RunScenarioF()
    {
        Console.WriteLine("\n=== Scenario F: Year boundary error ===");
        
        var settings = CreateBasicEmployeeSettings();
        int passCount = 0;

        for (int run = 1; run <= 5; run++)
        {
            Console.Write($"Run {run}/5... ");
            
            // Request crossing year boundary: Dec 31, 2025 -> Jan 1, 2026
            var request = new VacationRequest(
                new DateOnly(2025, 12, 31), // Wednesday
                new DateOnly(2026, 1, 1),   // Thursday
                false,
                false,
                settings.Workdays,
                new StudentParameters(false, "NW", new Dictionary<DayOfWeek, VocationalSchoolDayType>()),
                "NW",
                settings.Jahresurlaub,
                0m,
                2025
            );

            var result = _calculator.Calculate(request);

            // Verify: hard error
            if (!result.HasErrors)
            {
                Console.WriteLine("FAIL: Expected hard error for year crossing");
                return false;
            }

            // Verify error mentions year
            var errorMsg = string.Join(" ", result.Errors);
            if (!errorMsg.Contains("Jahr", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"FAIL: Error message should mention year: {errorMsg}");
                return false;
            }

            // Verify export blocked
            try
            {
                var entitlement = EntitlementSnapshot.From(settings.Jahresurlaub, 0m);
                var pdfPath = _pdfService.Export(settings, request, result, entitlement);
                
                Console.WriteLine("FAIL: PDF export should be blocked when errors exist");
                return false;
            }
            catch (InvalidOperationException)
            {
                // Expected
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL: Unexpected exception type: {ex.GetType().Name}");
                return false;
            }

            passCount++;
            Console.WriteLine("PASS");
        }

        Console.WriteLine($"Scenario F: {passCount}/5 passes - SUCCESS");
        return true;
    }

    private bool RunScenarioG()
    {
        Console.WriteLine("\n=== Scenario G: History workflow ===");
        
        var settings = CreateBasicEmployeeSettings();
        int passCount = 0;

        for (int run = 1; run <= 5; run++)
        {
            Console.Write($"Run {run}/5... ");
            
            // Clear ledger for clean test
            _ledgerService.Save(Array.Empty<LedgerEntry>());

            // Step 1: Create and export a request
            var request1 = new VacationRequest(
                new DateOnly(2025, 2, 3),
                new DateOnly(2025, 2, 7),
                false,
                false,
                settings.Workdays,
                new StudentParameters(false, "NW", new Dictionary<DayOfWeek, VocationalSchoolDayType>()),
                "NW",
                settings.Jahresurlaub,
                0m,
                2025
            );

            var result1 = _calculator.Calculate(request1);
            
            if (result1.HasErrors || result1.TotalDays != 5m)
            {
                Console.WriteLine($"FAIL (Step 1): Request creation failed");
                return false;
            }

            var entryId1 = Guid.NewGuid();
            var entry1 = new LedgerEntry(
                LedgerService.SchemaVersion,
                2025,
                entryId1,
                DateTimeOffset.Now,
                request1.StartDate,
                request1.EndDate,
                false,
                false,
                result1.TotalDays,
                VacationRequestStatus.Exported,
                null,
                null,
                null
            );

            // Step 2: Approve it
            var approvedEntry1 = entry1 with { Status = VacationRequestStatus.Approved };
            _ledgerService.Save(new[] { approvedEntry1 });

            var loaded1 = _ledgerService.Load();
            var approvedTotal = loaded1.GetApprovedTotal(2025);
            
            if (approvedTotal != 5m)
            {
                Console.WriteLine($"FAIL (Step 2): Expected approved total = 5, got {approvedTotal}");
                return false;
            }

            // Step 3: Create another request
            var request2 = new VacationRequest(
                new DateOnly(2025, 3, 10),
                new DateOnly(2025, 3, 14),
                false,
                false,
                settings.Workdays,
                new StudentParameters(false, "NW", new Dictionary<DayOfWeek, VocationalSchoolDayType>()),
                "NW",
                settings.Jahresurlaub,
                5m, // Already used 5 days
                2025
            );

            var result2 = _calculator.Calculate(request2);
            
            if (result2.HasErrors || result2.TotalDays != 5m)
            {
                Console.WriteLine($"FAIL (Step 3): Second request creation failed");
                return false;
            }

            var entryId2 = Guid.NewGuid();
            var entry2 = new LedgerEntry(
                LedgerService.SchemaVersion,
                2025,
                entryId2,
                DateTimeOffset.Now,
                request2.StartDate,
                request2.EndDate,
                false,
                false,
                result2.TotalDays,
                VacationRequestStatus.Exported,
                null,
                null,
                null
            );

            // Step 4: Reject the second request
            var rejectedEntry2 = entry2 with { Status = VacationRequestStatus.Rejected, RejectionReason = "Testgrund" };
            _ledgerService.Save(new[] { approvedEntry1, rejectedEntry2 });

            var loaded2 = _ledgerService.Load();
            var approvedTotal2 = loaded2.GetApprovedTotal(2025);
            
            // Should still be 5 (rejected not counted)
            if (approvedTotal2 != 5m)
            {
                Console.WriteLine($"FAIL (Step 4): Expected approved total = 5 (rejected not counted), got {approvedTotal2}");
                return false;
            }

            // Verify remaining days calculation
            var remaining = settings.Jahresurlaub - approvedTotal2;
            if (remaining != 25m) // 30 - 5 = 25
            {
                Console.WriteLine($"FAIL (Step 4): Expected remaining = 25, got {remaining}");
                return false;
            }

            passCount++;
            Console.WriteLine("PASS");
        }

        Console.WriteLine($"Scenario G: {passCount}/5 passes - SUCCESS");
        return true;
    }

    private AppSettings CreateBasicEmployeeSettings()
    {
        return new AppSettings
        {
            Name = "Test User",
            Abteilung = "IT",
            Klasse = "FI1",
            Jahresurlaub = 30m,
            Workdays = new HashSet<DayOfWeek> 
            { 
                DayOfWeek.Monday, 
                DayOfWeek.Tuesday, 
                DayOfWeek.Wednesday, 
                DayOfWeek.Thursday, 
                DayOfWeek.Friday 
            },
            StudentActive = false,
            Bundesland = "NW",
            VocationalSchool = Enum.GetValues<DayOfWeek>()
                .ToDictionary(d => d, _ => VocationalSchoolDayType.None)
        };
    }

    private class TestPathService : PathService
    {
        private readonly string _root;
        public TestPathService(string root) => _root = root;
        public override string GetAppDataDirectory() => _root;
        public override string GetExportDirectory() => Path.Combine(_root, "exports");
    }
}
