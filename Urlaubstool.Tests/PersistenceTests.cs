using System.Globalization;
using FluentAssertions;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.Ledger;
using Urlaubstool.Infrastructure.Paths;
using Urlaubstool.Infrastructure.Settings;

namespace Urlaubstool.Tests;

public class PersistenceTests
{
    [Fact]
    public async Task Settings_roundtrip_preserves_values()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var paths = new TestPathService(temp);
        var service = new SettingsService(paths);
        var original = new AppSettings
        {
            Name = "Max",
            Abteilung = "IT",
            Klasse = "FI",
            Jahresurlaub = 28.5m,
            Workdays = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday },
            StudentActive = true,
            Bundesland = "NW",
            VocationalSchool = new Dictionary<DayOfWeek, VocationalSchoolDayType> { { DayOfWeek.Monday, VocationalSchoolDayType.Half } }
        };

        await service.SaveAsync(original);
        var loaded = await service.LoadAsync();
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be(original.Name);
        loaded.Jahresurlaub.Should().Be(original.Jahresurlaub);
        loaded.Workdays.Should().BeEquivalentTo(original.Workdays);
        loaded.VocationalSchool.Should().ContainKey(DayOfWeek.Monday);
    }

    [Fact]
    public void Ledger_roundtrip_preserves_decimals()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var paths = new TestPathService(temp);
        var ledgerService = new LedgerService(paths);

        var entries = new List<LedgerEntry>
        {
            new(LedgerService.SchemaVersion, 2025, Guid.NewGuid(), DateTimeOffset.Now, new DateOnly(2025,1,1), new DateOnly(2025,1,2), false, false, 1.5m, VacationRequestStatus.Approved, null, null, "/tmp/test.pdf")
        };

        ledgerService.Save(entries);
        var loaded = ledgerService.Load();
        loaded.Entries.Should().HaveCount(1);
        loaded.Entries.First().DaysRequested.Should().Be(1.5m);
    }

    [Fact]
    public void LedgerEntry_export_creates_valid_PdfPath()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var paths = new TestPathService(temp);
        var ledgerService = new LedgerService(paths);

        var pdfPath = "/Users/test/Desktop/Urlaubsantrag_2025-01-10_bis_2025-01-14_v1.pdf";
        var entry = new LedgerEntry(
            LedgerService.SchemaVersion,
            2025,
            Guid.NewGuid(),
            DateTimeOffset.Now,
            new DateOnly(2025, 1, 10),
            new DateOnly(2025, 1, 14),
            false,
            false,
            5.0m,
            VacationRequestStatus.Exported,
            null,           // RejectionReason
            null,           // ArchivedAt
            pdfPath         // PdfPath
        );

        ledgerService.Save(new[] { entry });
        var loaded = ledgerService.Load();
        
        loaded.Entries.Should().HaveCount(1);
        loaded.Entries.First().PdfPath.Should().Be(pdfPath);
        loaded.Entries.First().RejectionReason.Should().BeNull();
    }

    [Fact]
    public void LedgerService_migrates_legacy_data_with_PdfPath_in_RejectionReason()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var paths = new TestPathService(temp);
        
        // Manually create CSV with old broken format (PdfPath in RejectionReason column)
        var csvPath = paths.GetLedgerFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
        
        var legacyPdfPath = "/Users/test/Desktop/Urlaubsantrag_2025-01-10_bis_2025-01-14_v1.pdf";
        var csvContent = $@"SchemaVersion,Year,RequestId,CreatedAt,StartDate,EndDate,StartHalfDay,EndHalfDay,DaysRequested,Status,RejectionReason,ArchivedAt,PdfPath
1,2025,{Guid.NewGuid()},2025-01-10T10:00:00+00:00,2025-01-10,2025-01-14,False,False,5,Exported,{legacyPdfPath},,";
        
        File.WriteAllText(csvPath, csvContent);
        
        // Load and verify migration
        var ledgerService = new LedgerService(paths);
        var loaded = ledgerService.Load();
        
        loaded.Entries.Should().HaveCount(1);
        var entry = loaded.Entries.First();
        
        // Migration should move PDF path from RejectionReason to PdfPath
        entry.PdfPath.Should().Be(legacyPdfPath);
        entry.RejectionReason.Should().BeNull();
    }

    [Fact]
    public void GetExportDirectory_uses_documents_on_windows()
    {
        var paths = new WindowsPathService("C:/Users/Test/Documents", "C:/Users/Test/AppData/Roaming");

        paths.GetExportDirectory().Should().Be(Path.Combine("C:/Users/Test/Documents", "Urlaubstool", "Exports"));
    }

    [Fact]
    public void GetExportDirectory_uses_appdata_on_non_windows()
    {
        var paths = new NonWindowsPathService("/Users/test/Documents", "/Users/test/.config");

        paths.GetExportDirectory().Should().Be(Path.Combine("/Users/test/.config", "Urlaubstool", "Exports"));
    }

    private sealed class TestPathService : PathService
    {
        private readonly string _root;
        public TestPathService(string root) => _root = root;
        public override string GetAppDataDirectory() => _root;
        public override string GetExportDirectory() => Path.Combine(_root, "exports");
    }

    private sealed class WindowsPathService : PathService
    {
        private readonly string _documents;
        private readonly string _appData;

        public WindowsPathService(string documents, string appData)
        {
            _documents = documents;
            _appData = appData;
        }

        protected override bool IsWindows() => true;
        protected override bool IsMacOs() => false;
        protected override string GetDocumentsDirectory() => _documents;
        protected override string GetRoamingAppDataDirectory() => _appData;
    }

    private sealed class NonWindowsPathService : PathService
    {
        private readonly string _documents;
        private readonly string _appData;

        public NonWindowsPathService(string documents, string appData)
        {
            _documents = documents;
            _appData = appData;
        }

        protected override bool IsWindows() => false;
        protected override bool IsMacOs() => false;
        protected override string GetDocumentsDirectory() => _documents;
        protected override string GetRoamingAppDataDirectory() => _appData;
    }
}
