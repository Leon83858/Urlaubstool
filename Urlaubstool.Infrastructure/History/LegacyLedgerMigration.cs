using Microsoft.Extensions.Logging;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.Ledger;
using Urlaubstool.Infrastructure.Paths;

namespace Urlaubstool.Infrastructure.History;

/// <summary>
/// Migrates legacy ledger.csv to new history.jsonl format.
/// </summary>
public sealed class LegacyLedgerMigration
{
    private readonly PathService _paths;
    private readonly IHistoryStore _historyStore;
    private readonly LedgerService _legacyLedgerService;
    private readonly ILogger<LegacyLedgerMigration> _logger;

    public LegacyLedgerMigration(
        PathService paths,
        IHistoryStore historyStore,
        LedgerService legacyLedgerService,
        ILogger<LegacyLedgerMigration> logger)
    {
        _paths = paths;
        _historyStore = historyStore;
        _legacyLedgerService = legacyLedgerService;
        _logger = logger;
    }

    /// <summary>
    /// Checks if migration is needed and performs it.
    /// Idempotent: can be called multiple times safely.
    /// </summary>
    public async Task MigrateIfNeededAsync()
    {
        var historyPath = _paths.GetHistoryFilePath();
        var ledgerPath = _paths.GetLedgerFilePath();

        // Migration needed if history is missing OR empty
        if (File.Exists(historyPath))
        {
            var existingEvents = await _historyStore.LoadAsync();
            if (existingEvents.Count > 0)
            {
                _logger.LogInformation("History file already exists with {Count} events, skipping migration", existingEvents.Count);
                return;
            }
            _logger.LogWarning("History file exists but is empty. Migration will run if ledger.csv is present.");
        }

        if (!File.Exists(ledgerPath))
        {
            _logger.LogInformation("No legacy ledger file found, skipping migration");
            return;
        }

        _logger.LogInformation("Starting migration from ledger.csv to history.jsonl");

        try
        {
            var snapshot = _legacyLedgerService.Load();
            var migratedCount = 0;

            foreach (var entry in snapshot.Entries)
            {
                await MigrateLedgerEntryAsync(entry);
                migratedCount++;
            }

            // Rename legacy file to prevent re-migration
            var backupPath = ledgerPath + ".migrated.bak";
            File.Move(ledgerPath, backupPath);

            _logger.LogInformation("Migration completed: {Count} entries migrated, legacy file renamed to {BackupPath}",
                migratedCount, backupPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Migration failed");
            throw new InvalidOperationException($"Failed to migrate legacy ledger: {ex.Message}", ex);
        }
    }

    private async Task MigrateLedgerEntryAsync(LedgerEntry entry)
    {
        // Create event
        var createdEvent = new VacationRequestCreatedEvent(
            EventId: Guid.NewGuid(),
            RequestId: entry.RequestId,
            Timestamp: entry.CreatedAt,
            Year: entry.Year,
            StartDate: entry.StartDate,
            EndDate: entry.EndDate,
            StartHalfDay: entry.StartHalfDay,
            EndHalfDay: entry.EndHalfDay,
            CalculatedDays: entry.DaysRequested);

        await _historyStore.AppendAsync(createdEvent);

        // Status event
        switch (entry.Status)
        {
            case VacationRequestStatus.Exported:
                if (!string.IsNullOrWhiteSpace(entry.PdfPath))
                {
                    var exportedEvent = new VacationRequestExportedEvent(
                        EventId: Guid.NewGuid(),
                        RequestId: entry.RequestId,
                        Timestamp: entry.CreatedAt.AddSeconds(1),
                        PdfPath: entry.PdfPath);
                    await _historyStore.AppendAsync(exportedEvent);
                }
                break;

            case VacationRequestStatus.Approved:
                // First export (if we have a PDF path)
                if (!string.IsNullOrWhiteSpace(entry.PdfPath))
                {
                    var exportedEvent = new VacationRequestExportedEvent(
                        EventId: Guid.NewGuid(),
                        RequestId: entry.RequestId,
                        Timestamp: entry.CreatedAt.AddSeconds(1),
                        PdfPath: entry.PdfPath);
                    await _historyStore.AppendAsync(exportedEvent);
                }

                // Then approve
                var approvedEvent = new VacationRequestApprovedEvent(
                    EventId: Guid.NewGuid(),
                    RequestId: entry.RequestId,
                    Timestamp: entry.CreatedAt.AddSeconds(2));
                await _historyStore.AppendAsync(approvedEvent);
                break;

            case VacationRequestStatus.Rejected:
                // First export (if we have a PDF path)
                if (!string.IsNullOrWhiteSpace(entry.PdfPath))
                {
                    var exportedEvent = new VacationRequestExportedEvent(
                        EventId: Guid.NewGuid(),
                        RequestId: entry.RequestId,
                        Timestamp: entry.CreatedAt.AddSeconds(1),
                        PdfPath: entry.PdfPath);
                    await _historyStore.AppendAsync(exportedEvent);
                }

                // Then reject
                var rejectedEvent = new VacationRequestRejectedEvent(
                    EventId: Guid.NewGuid(),
                    RequestId: entry.RequestId,
                    Timestamp: entry.CreatedAt.AddSeconds(2),
                    RejectionReason: entry.RejectionReason ?? "Keine Begründung");
                await _historyStore.AppendAsync(rejectedEvent);
                break;

            case VacationRequestStatus.Archived:
                // Export -> Approve -> Archive sequence
                if (!string.IsNullOrWhiteSpace(entry.PdfPath))
                {
                    var exportedEvent = new VacationRequestExportedEvent(
                        EventId: Guid.NewGuid(),
                        RequestId: entry.RequestId,
                        Timestamp: entry.CreatedAt.AddSeconds(1),
                        PdfPath: entry.PdfPath);
                    await _historyStore.AppendAsync(exportedEvent);
                }

                var approvedBeforeArchive = new VacationRequestApprovedEvent(
                    EventId: Guid.NewGuid(),
                    RequestId: entry.RequestId,
                    Timestamp: entry.CreatedAt.AddSeconds(2));
                await _historyStore.AppendAsync(approvedBeforeArchive);

                var archivedEvent = new VacationRequestArchivedEvent(
                    EventId: Guid.NewGuid(),
                    RequestId: entry.RequestId,
                    Timestamp: entry.ArchivedAt ?? entry.CreatedAt.AddSeconds(3));
                await _historyStore.AppendAsync(archivedEvent);
                break;
        }

        _logger.LogDebug("Migrated entry {RequestId} with status {Status}", 
            entry.RequestId, entry.Status);
    }
}
