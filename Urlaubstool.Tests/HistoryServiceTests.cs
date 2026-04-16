using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.History;
using Urlaubstool.Infrastructure.Ledger;
using Urlaubstool.Infrastructure.Logging;
using Urlaubstool.Infrastructure.Paths;
using Xunit;

namespace Urlaubstool.Tests;

public class HistoryServiceTests
{
    [Fact]
    public async Task Store_AppendAndLoad_RoundTrip()
    {
        // Arrange
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var paths = new TestPathService(temp);
        var logger = new ConsoleLogger<JsonlHistoryStore>();
        var store = new JsonlHistoryStore(paths, logger);

        var @event = new VacationRequestCreatedEvent(
            EventId: Guid.NewGuid(),
            RequestId: Guid.NewGuid(),
            Timestamp: DateTimeOffset.Now,
            Year: 2025,
            StartDate: new DateOnly(2025, 1, 10),
            EndDate: new DateOnly(2025, 1, 14),
            StartHalfDay: false,
            EndHalfDay: false,
            CalculatedDays: 5.0m);

        // Act
        await store.AppendAsync(@event);
        var loaded = await store.LoadAsync();

        // Assert
        loaded.Should().HaveCount(1);
        loaded.First().Should().BeOfType<VacationRequestCreatedEvent>();
        var loadedEvent = (VacationRequestCreatedEvent)loaded.First();
        loadedEvent.RequestId.Should().Be(@event.RequestId);
        loadedEvent.Year.Should().Be(2025);
        loadedEvent.CalculatedDays.Should().Be(5.0m);

        // Cleanup
        Directory.Delete(temp, true);
    }

    [Fact]
    public async Task Projection_CreatedThenExported_SetsStatus()
    {
        // Arrange
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var paths = new TestPathService(temp);
        var storeLogger = new ConsoleLogger<JsonlHistoryStore>();
        var serviceLogger = new ConsoleLogger<HistoryService>();
        var store = new JsonlHistoryStore(paths, storeLogger);
        var service = new HistoryService(store, serviceLogger);

        var dto = new CreateVacationRequestDto(
            Year: 2025,
            StartDate: new DateOnly(2025, 1, 10),
            EndDate: new DateOnly(2025, 1, 14),
            StartHalfDay: false,
            EndHalfDay: false,
            CalculatedDays: 5.0m);

        // Act
        var requestId = await service.CreateAsync(dto);
        await service.MarkExportedAsync(requestId, "/tmp/test.pdf");

        var entries = await service.GetEntriesAsync(2025, StatusFilter.All);

        // Assert
        entries.Should().HaveCount(1);
        entries.First().RequestId.Should().Be(requestId);
        entries.First().Status.Should().Be(VacationRequestStatus.Exported);
        entries.First().PdfPath.Should().Be("/tmp/test.pdf");

        // Cleanup
        Directory.Delete(temp, true);
    }

    [Fact]
    public async Task Projection_ApprovedEntry_FilterWorks()
    {
        // Arrange
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var paths = new TestPathService(temp);
        var storeLogger = new ConsoleLogger<JsonlHistoryStore>();
        var serviceLogger = new ConsoleLogger<HistoryService>();
        var store = new JsonlHistoryStore(paths, storeLogger);
        var service = new HistoryService(store, serviceLogger);

        var dto = new CreateVacationRequestDto(
            Year: 2025,
            StartDate: new DateOnly(2025, 1, 10),
            EndDate: new DateOnly(2025, 1, 14),
            StartHalfDay: false,
            EndHalfDay: false,
            CalculatedDays: 5.0m);

        // Act
        var requestId = await service.CreateAsync(dto);
        await service.MarkExportedAsync(requestId, "/tmp/test.pdf");
        await service.MarkApprovedAsync(requestId);

        var allEntries = await service.GetEntriesAsync(2025, StatusFilter.All);
        var approvedEntries = await service.GetEntriesAsync(2025, StatusFilter.Approved);
        var exportedEntries = await service.GetEntriesAsync(2025, StatusFilter.Exported);

        // Assert
        allEntries.Should().HaveCount(1);
        approvedEntries.Should().HaveCount(1);
        exportedEntries.Should().HaveCount(0);

        approvedEntries.First().Status.Should().Be(VacationRequestStatus.Approved);

        // Cleanup
        Directory.Delete(temp, true);
    }

    [Fact]
    public void LedgerService_Can_Read_SimpleCSV()
    {
        // Test CSV parsing - ensure we have the correct number of columns
        var testLine = "1,2025,12345678-1234-1234-1234-123456789012,2025-01-10T10:00:00+00:00,2025-01-10,2025-01-14,False,False,5.0,Exported,,,/Users/test/Desktop/test.pdf";
        
        // Use reflection to call the private method
        var method = typeof(LedgerService).GetMethod("ParseCsvLine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = method?.Invoke(null, new object[] { testLine }) as string[];
        
        if (result == null || result.Length != 13)
        {
            throw new Exception($"ParseCsvLine returned {result?.Length ?? 0} cells instead of 13 for line: {testLine}");
        }
        
        // Now test the full flow
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var paths = new TestPathService(temp);

        var csvPath = paths.GetLedgerFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);

        var requestId = Guid.NewGuid();
        var pdfPath = "/Users/test/Desktop/test.pdf";
        
        var header = "SchemaVersion,Year,RequestId,CreatedAt,StartDate,EndDate,StartHalfDay,EndHalfDay,DaysRequested,Status,RejectionReason,ArchivedAt,PdfPath";
        var dataRow = $"1,2025,{requestId},2025-01-10T10:00:00+00:00,2025-01-10,2025-01-14,False,False,5.0,Exported,,,{pdfPath}";
        var csvContent = header + "\n" + dataRow;

        File.WriteAllText(csvPath, csvContent);
        
        var ledgerService = new LedgerService(paths);
        var snapshot = ledgerService.Load();

        snapshot.Entries.Should().HaveCount(1);
        snapshot.Entries.First().RequestId.Should().Be(requestId);
        snapshot.Entries.First().Status.Should().Be(VacationRequestStatus.Exported);
        snapshot.Entries.First().PdfPath.Should().Be(pdfPath);

        Directory.Delete(temp, true);
    }


    [Fact]
    public async Task Migration_LegacyLedger_ImportsCorrectly()
    {
        // Arrange
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var paths = new TestPathService(temp);

        // Create legacy ledger.csv
        var csvPath = paths.GetLedgerFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);

        var requestId = Guid.NewGuid();
        var pdfPath = "/Users/test/Desktop/test.pdf";
        var csvContent = $@"SchemaVersion,Year,RequestId,CreatedAt,StartDate,EndDate,StartHalfDay,EndHalfDay,DaysRequested,Status,RejectionReason,ArchivedAt,PdfPath
1,2025,{requestId},2025-01-10T10:00:00+00:00,2025-01-10,2025-01-14,False,False,5.0,Exported,,,{pdfPath}";

        File.WriteAllText(csvPath, csvContent);

        var storeLogger = new ConsoleLogger<JsonlHistoryStore>();
        var migrationLogger = new ConsoleLogger<LegacyLedgerMigration>();
        var store = new JsonlHistoryStore(paths, storeLogger);
        var legacyService = new LedgerService(paths);
        var migration = new LegacyLedgerMigration(paths, store, legacyService, migrationLogger);

        // Act
        await migration.MigrateIfNeededAsync();

        // Verify migration
        File.Exists(csvPath).Should().BeFalse();
        File.Exists(csvPath + ".migrated.bak").Should().BeTrue();

        // Check if history file was created
        var historyPath = paths.GetHistoryFilePath();
        if (!File.Exists(historyPath))
        {
            // Migration might have created 0 events
            // Try calling migration again with fresh store to see if it recreates
            var newStore2 = new JsonlHistoryStore(paths, storeLogger);
            var events2 = await newStore2.LoadAsync();
            throw new Exception($"History file does not exist after migration. Events loaded: {events2.Count}");
        }

        var historyFileContent = File.ReadAllText(historyPath);
        if (string.IsNullOrWhiteSpace(historyFileContent))
        {
            throw new Exception($"History file is empty after migration");
        }

        // Create a new store to load migrated events
        var newStore = new JsonlHistoryStore(paths, storeLogger);
        var events = await newStore.LoadAsync();
        
        // If no events, something is wrong with migration or store
        if (events.Count == 0)
        {
            throw new Exception($"Store loaded 0 events from history file. History file content:\n{historyFileContent}");
        }
        
        events.Should().HaveCount(2); // Created + Exported

        var serviceLogger = new ConsoleLogger<HistoryService>();
        var service = new HistoryService(newStore, serviceLogger);
        var entries = await service.GetEntriesAsync(2025, StatusFilter.All);

        entries.Should().HaveCount(1);
        entries.First().RequestId.Should().Be(requestId);
        entries.First().Status.Should().Be(VacationRequestStatus.Exported);
        entries.First().PdfPath.Should().Be(pdfPath);

        // Cleanup
        Directory.Delete(temp, true);
    }

    private sealed class TestPathService : PathService
    {
        private readonly string _tempDirectory;

        public TestPathService(string tempDirectory)
        {
            _tempDirectory = tempDirectory;
        }

        public override string GetAppDataDirectory() => _tempDirectory;
    }
}
