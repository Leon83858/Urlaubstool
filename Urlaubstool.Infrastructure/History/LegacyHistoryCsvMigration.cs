using System.Globalization;
using Microsoft.Extensions.Logging;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.Paths;

namespace Urlaubstool.Infrastructure.History;

/// <summary>
/// Migrates legacy history.csv (old CSV-based event store) to the canonical history.jsonl format.
/// 
/// This migration:
/// 1. Checks if history.jsonl already exists with events (if so, skips)
/// 2. If history.csv exists, reads and parses all valid events
/// 3. Appends them into history.jsonl via JsonlHistoryStore
/// 4. Renames history.csv to history.legacy.csv to prevent re-migration
/// 5. Implements de-duplication to prevent duplicate events
/// 
/// This is a one-time migration that runs during app startup.
/// </summary>
public sealed class LegacyHistoryCsvMigration
{
    private readonly PathService _paths;
    private readonly IHistoryStore _jsonlHistoryStore;
    private readonly ILogger<LegacyHistoryCsvMigration> _logger;

    public LegacyHistoryCsvMigration(
        PathService paths,
        IHistoryStore jsonlHistoryStore,
        ILogger<LegacyHistoryCsvMigration> logger)
    {
        _paths = paths;
        _jsonlHistoryStore = jsonlHistoryStore;
        _logger = logger;
    }

    /// <summary>
    /// Checks if migration is needed and performs it.
    /// Idempotent: can be called multiple times safely.
    /// 
    /// Migration logic:
    /// - If history.jsonl exists and has >= 1 valid event => do nothing
    /// - Else if history.csv exists => migrate all valid events to history.jsonl, rename CSV to legacy
    /// - Else => do nothing (no migration needed)
    /// </summary>
    public async Task MigrateIfNeededAsync()
    {
        var historyJsonlPath = _paths.GetHistoryFilePath();
        var historyCsvPath = Path.Combine(_paths.GetAppDataDirectory(), "history.csv");

        // Check if JSONL history already exists with events
        if (File.Exists(historyJsonlPath))
        {
            try
            {
                var existingEvents = await _jsonlHistoryStore.LoadAsync();
                if (existingEvents.Count > 0)
                {
                    _logger.LogInformation(
                        "History JSONL file already exists with {Count} events, skipping CSV migration",
                        existingEvents.Count);
                    return;
                }
                _logger.LogWarning(
                    "History JSONL file exists but is empty. Will attempt CSV migration if CSV file is present.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check existing JSONL history, skipping migration");
                return;
            }
        }

        // Check if CSV file exists
        if (!File.Exists(historyCsvPath))
        {
            _logger.LogInformation("No legacy CSV history file found, skipping migration");
            return;
        }

        _logger.LogInformation("Starting migration from history.csv to history.jsonl");

        try
        {
            // Load existing events from JSONL if it exists (for de-duplication)
            var existingEventIds = new HashSet<Guid>();
            var existingRequestIds = new HashSet<(Guid RequestId, string EventType, DateTimeOffset Timestamp)>();
            
            if (File.Exists(historyJsonlPath))
            {
                try
                {
                    var existing = await _jsonlHistoryStore.LoadAsync();
                    foreach (var evt in existing)
                    {
                        existingEventIds.Add(evt.EventId);
                        existingRequestIds.Add((evt.RequestId, evt.GetType().Name, evt.Timestamp));
                    }
                    _logger.LogInformation("Loaded {Count} existing events for de-duplication", existingEventIds.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load existing events for de-duplication, continuing anyway");
                }
            }

            // Parse CSV and migrate valid events
            var csvEvents = await ParseCsvHistoryAsync(historyCsvPath);
            var migratedCount = 0;
            var skippedCount = 0;
            var badLines = new List<string>();

            foreach (var (line, @event) in csvEvents)
            {
                if (@event == null)
                {
                    badLines.Add(line);
                    skippedCount++;
                    continue;
                }

                // De-duplication checks
                if (existingEventIds.Contains(@event.EventId))
                {
                    _logger.LogWarning(
                        "Skipping duplicate event {EventId} for request {RequestId}",
                        @event.EventId, @event.RequestId);
                    skippedCount++;
                    continue;
                }

                var eventTypeName = @event.GetType().Name;
                if (existingRequestIds.Contains((@event.RequestId, eventTypeName, @event.Timestamp)))
                {
                    _logger.LogWarning(
                        "Skipping duplicate event {EventType} for request {RequestId} at {Timestamp}",
                        eventTypeName, @event.RequestId, @event.Timestamp);
                    skippedCount++;
                    continue;
                }

                // Append to JSONL
                try
                {
                    await _jsonlHistoryStore.AppendAsync(@event);
                    existingEventIds.Add(@event.EventId);
                    existingRequestIds.Add((@event.RequestId, eventTypeName, @event.Timestamp));
                    migratedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to migrate event {EventId}, treating as bad line", @event.EventId);
                    badLines.Add(line);
                    skippedCount++;
                }
            }

            // Write bad lines to bad history file if any
            if (badLines.Count > 0)
            {
                var badCsvPath = Path.Combine(_paths.GetAppDataDirectory(), "history.bad.csv");
                await File.WriteAllLinesAsync(badCsvPath, badLines);
                _logger.LogWarning("Wrote {Count} unparseable lines to {BadPath}", badLines.Count, badCsvPath);
            }

            // Rename CSV to prevent re-migration
            var legacyPath = historyCsvPath + ".migrated.bak";
            File.Move(historyCsvPath, legacyPath, overwrite: true);

            _logger.LogInformation(
                "Migration completed: {Migrated} events migrated, {Skipped} skipped, legacy file renamed to {BackupPath}",
                migratedCount, skippedCount, legacyPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Migration failed");
            throw new InvalidOperationException($"Failed to migrate CSV history: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Parses CSV history file and returns list of (line, event) tuples.
    /// Returns nulls for unparseable lines.
    /// </summary>
    private async Task<List<(string Line, HistoryEvent? Event)>> ParseCsvHistoryAsync(string csvPath)
    {
        var results = new List<(string, HistoryEvent?)>();

        var lines = await File.ReadAllLinesAsync(csvPath);
        var lineNumber = 0;

        foreach (var line in lines)
        {
            lineNumber++;

            // Skip header
            if (lineNumber == 1)
                continue;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var @event = CsvLineToEvent(line);
                results.Add((line, @event));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse CSV line {LineNumber}", lineNumber);
                results.Add((line, null));
            }
        }

        return results;
    }

    /// <summary>
    /// Converts a CSV line to a HistoryEvent (copied and adapted from CsvHistoryStore).
    /// Handles decimal parsing robustly.
    /// </summary>
    private HistoryEvent? CsvLineToEvent(string line)
    {
        var fields = CsvParseLine(line);
        if (fields.Length < 4)
        {
            _logger.LogWarning("CSV line has insufficient fields: {FieldCount}", fields.Length);
            return null;
        }

        try
        {
            var eventId = Guid.Parse(fields[0]);
            var requestId = Guid.Parse(fields[1]);
            var timestamp = DateTimeOffset.Parse(fields[2]);
            var eventType = fields[3];

            return eventType switch
            {
                "Created" => new VacationRequestCreatedEvent(
                    EventId: eventId,
                    RequestId: requestId,
                    Timestamp: timestamp,
                    Year: int.Parse(fields[4]),
                    StartDate: DateOnly.Parse(fields[5]),
                    EndDate: DateOnly.Parse(fields[6]),
                    StartHalfDay: bool.Parse(fields[7]),
                    EndHalfDay: bool.Parse(fields[8]),
                    // FIX: Use decimal.Parse instead of int.Parse to support half days
                    CalculatedDays: ParseCalculatedDays(fields[9])
                ),
                "Exported" => new VacationRequestExportedEvent(
                    EventId: eventId,
                    RequestId: requestId,
                    Timestamp: timestamp,
                    PdfPath: fields.Length > 10 ? fields[10] : null
                ),
                "Approved" => new VacationRequestApprovedEvent(
                    EventId: eventId,
                    RequestId: requestId,
                    Timestamp: timestamp
                ),
                "Rejected" => new VacationRequestRejectedEvent(
                    EventId: eventId,
                    RequestId: requestId,
                    Timestamp: timestamp,
                    RejectionReason: fields.Length > 11 ? fields[11] : null
                ),
                "Archived" => new VacationRequestArchivedEvent(
                    EventId: eventId,
                    RequestId: requestId,
                    Timestamp: timestamp
                ),
                "Deleted" => new VacationRequestDeletedEvent(
                    EventId: eventId,
                    RequestId: requestId,
                    Timestamp: timestamp
                ),
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse event from CSV line");
            return null;
        }
    }

    /// <summary>
    /// Safely parses CalculatedDays field, supporting both decimals and integers.
    /// Returns 0m if field is empty or parsing fails.
    /// </summary>
    private decimal ParseCalculatedDays(string field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            _logger.LogWarning("CalculatedDays field is empty, treating as 0");
            return 0m;
        }

        if (decimal.TryParse(field, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        _logger.LogWarning("Failed to parse CalculatedDays '{Field}' as decimal, treating as 0", field);
        return 0m;
    }

    /// <summary>
    /// Simple CSV line parser that handles quoted fields.
    /// Splits on comma but respects quoted values.
    /// </summary>
    private string[] CsvParseLine(string line)
    {
        var fields = new List<string>();
        var currentField = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(currentField.ToString().Trim('"').Trim());
                currentField.Clear();
            }
            else
            {
                currentField.Append(c);
            }
        }

        fields.Add(currentField.ToString().Trim('"').Trim());
        return fields.ToArray();
    }
}
