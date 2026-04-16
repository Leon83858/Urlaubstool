using System.Globalization;
using Microsoft.Extensions.Logging;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.Paths;

namespace Urlaubstool.Infrastructure.History;

/// <summary>
/// CSV-based history store with atomic writes.
/// Each row represents a single event with all properties in columns.
/// </summary>
public sealed class CsvHistoryStore : IHistoryStore
{
    private readonly PathService _paths;
    private readonly ILogger<CsvHistoryStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public CsvHistoryStore(PathService paths, ILogger<CsvHistoryStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task AppendAsync(HistoryEvent @event)
    {
        var path = _paths.GetHistoryFilePath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        await _writeLock.WaitAsync();
        try
        {
            // Create file with header if it doesn't exist
            if (!File.Exists(path))
            {
                var header = "EventId,RequestId,Timestamp,EventType,Year,StartDate,EndDate,StartHalfDay,EndHalfDay,CalculatedDays,PdfPath,RejectionReason";
                await File.WriteAllTextAsync(path, header + Environment.NewLine);
            }

            var csvLine = EventToCsvLine(@event);
            await File.AppendAllTextAsync(path, csvLine + Environment.NewLine);
            
            _logger.LogInformation("Appended event {EventType} for request {RequestId}", 
                @event.GetType().Name, @event.RequestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append event {EventType} for request {RequestId}", 
                @event.GetType().Name, @event.RequestId);
            throw new InvalidOperationException($"Failed to append history event: {ex.Message}", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<HistoryEvent>> LoadAsync()
    {
        var path = _paths.GetHistoryFilePath();
        if (!File.Exists(path))
        {
            _logger.LogInformation("History file does not exist yet: {Path}", path);
            return Array.Empty<HistoryEvent>();
        }

        var events = new List<HistoryEvent>();
        var badLines = new List<string>();
        var lineNumber = 0;

        try
        {
            var lines = await File.ReadAllLinesAsync(path);
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
                    if (@event != null)
                    {
                        events.Add(@event);
                    }
                    else
                    {
                        _logger.LogWarning("Null event parsed at line {LineNumber}", lineNumber);
                        badLines.Add(line);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse event at line {LineNumber}", lineNumber);
                    badLines.Add(line);
                }
            }

            if (badLines.Count > 0)
            {
                _logger.LogWarning("Found {Count} bad lines", badLines.Count);
            }

            // Sort by timestamp for stable event ordering
            events.Sort((a, b) => DateTimeOffset.Compare(a.Timestamp, b.Timestamp));
            
            _logger.LogInformation("Loaded {Count} events from history", events.Count);
            return events;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load history from {Path}", path);
            throw new InvalidOperationException($"Failed to load history: {ex.Message}", ex);
        }
    }

    private string EventToCsvLine(HistoryEvent @event)
    {
        var eventType = @event switch
        {
            VacationRequestCreatedEvent => "Created",
            VacationRequestExportedEvent => "Exported",
            VacationRequestApprovedEvent => "Approved",
            VacationRequestRejectedEvent => "Rejected",
            VacationRequestArchivedEvent => "Archived",
            VacationRequestDeletedEvent => "Deleted",
            _ => throw new InvalidOperationException($"Unknown event type: {@event.GetType().Name}")
        };

        // Build CSV line with proper escaping
        var fields = new List<string>
        {
            CsvEscape(@event.EventId.ToString()),
            CsvEscape(@event.RequestId.ToString()),
            CsvEscape(@event.Timestamp.ToString("O")),
            CsvEscape(eventType)
        };

        // Add event-specific fields
        if (@event is VacationRequestCreatedEvent created)
        {
            fields.Add(created.Year.ToString());
            fields.Add(CsvEscape(created.StartDate.ToString("O")));
            fields.Add(CsvEscape(created.EndDate.ToString("O")));
            fields.Add(created.StartHalfDay.ToString());
            fields.Add(created.EndHalfDay.ToString());
            fields.Add(created.CalculatedDays.ToString());
            fields.Add(""); // PdfPath
            fields.Add(""); // RejectionReason
        }
        else if (@event is VacationRequestExportedEvent exported)
        {
            fields.Add(""); // Year
            fields.Add(""); // StartDate
            fields.Add(""); // EndDate
            fields.Add(""); // StartHalfDay
            fields.Add(""); // EndHalfDay
            fields.Add(""); // CalculatedDays
            fields.Add(CsvEscape(exported.PdfPath));
            fields.Add(""); // RejectionReason
        }
        else if (@event is VacationRequestRejectedEvent rejected)
        {
            fields.Add(""); // Year
            fields.Add(""); // StartDate
            fields.Add(""); // EndDate
            fields.Add(""); // StartHalfDay
            fields.Add(""); // EndHalfDay
            fields.Add(""); // CalculatedDays
            fields.Add(""); // PdfPath
            fields.Add(CsvEscape(rejected.RejectionReason));
        }
        else
        {
            // Other event types have no additional fields
            fields.Add(""); // Year
            fields.Add(""); // StartDate
            fields.Add(""); // EndDate
            fields.Add(""); // StartHalfDay
            fields.Add(""); // EndHalfDay
            fields.Add(""); // CalculatedDays
            fields.Add(""); // PdfPath
            fields.Add(""); // RejectionReason
        }

        return string.Join(",", fields);
    }

    private HistoryEvent? CsvLineToEvent(string line)
    {
        var fields = ParseCsvLine(line);
        if (fields.Count < 4)
            return null;

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
                // FIX: Parse as decimal to support half days (0.5, 1.5, etc.)
                // Use InvariantCulture to handle both "1.5" and "1,5" formats
                CalculatedDays: ParseCalculatedDays(fields[9])
            ),
            "Exported" => new VacationRequestExportedEvent(
                EventId: eventId,
                RequestId: requestId,
                Timestamp: timestamp,
                PdfPath: fields[10]
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
                RejectionReason: fields[11]
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

    private string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
            
        // Escape quotes and wrap in quotes if contains comma, quote, or newline
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
    }

    private List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var currentField = "";
        var inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    // Escaped quote
                    currentField += '"';
                    i++; // Skip next quote
                }
                else
                {
                    // Toggle quote mode
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                // Field separator
                fields.Add(currentField);
                currentField = "";
            }
            else
            {
                currentField += c;
            }
        }

        // Add last field
        fields.Add(currentField);

        return fields;
    }

    /// <summary>
    /// Safely parses CalculatedDays field as decimal.
    /// Supports both full days and half days (0.5, 1.5, 2.0, etc.)
    /// Uses InvariantCulture to handle different decimal formats.
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
}
