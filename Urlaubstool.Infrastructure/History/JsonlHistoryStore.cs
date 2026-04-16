using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.Paths;

namespace Urlaubstool.Infrastructure.History;

/// <summary>
/// Interface for storing and loading history events.
/// </summary>
public interface IHistoryStore
{
    Task AppendAsync(HistoryEvent @event);
    Task<IReadOnlyList<HistoryEvent>> LoadAsync();
}

/// <summary>
/// JSONL-based history store with atomic writes and error recovery.
/// Each line is a single JSON-serialized event.
/// </summary>
public sealed class JsonlHistoryStore : IHistoryStore
{
    private readonly PathService _paths;
    private readonly ILogger<JsonlHistoryStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions;

    public JsonlHistoryStore(PathService paths, ILogger<JsonlHistoryStore> logger)
    {
        _paths = paths;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = HistoryJsonContext.Default,
            Converters =
            {
                new JsonStringEnumConverter(),
                new HistoryEventJsonConverter()
            }
        };
    }

    public async Task AppendAsync(HistoryEvent @event)
    {
        var path = _paths.GetHistoryFilePath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        await _writeLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(@event, _jsonOptions);
            await File.AppendAllTextAsync(path, json + Environment.NewLine);
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
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var @event = JsonSerializer.Deserialize<HistoryEvent>(line, _jsonOptions);
                    if (@event != null)
                    {
                        events.Add(@event);
                    }
                    else
                    {
                        _logger.LogWarning("Null event deserialized at line {LineNumber}", lineNumber);
                        badLines.Add(line);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize event at line {LineNumber}", lineNumber);
                    badLines.Add(line);
                }
            }

            if (badLines.Count > 0)
            {
                await SaveBadLinesAsync(badLines);
                _logger.LogWarning("Found {Count} bad lines, saved to {Path}", 
                    badLines.Count, _paths.GetBadHistoryFilePath());
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

    private async Task SaveBadLinesAsync(List<string> badLines)
    {
        var badPath = _paths.GetBadHistoryFilePath();
        var directory = Path.GetDirectoryName(badPath)!;
        Directory.CreateDirectory(directory);

        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        await File.AppendAllTextAsync(badPath, 
            $"# Bad lines detected at {timestamp}{Environment.NewLine}");
        foreach (var line in badLines)
        {
            await File.AppendAllTextAsync(badPath, line + Environment.NewLine);
        }
    }
}

/// <summary>
/// Custom JSON converter for polymorphic HistoryEvent deserialization.
/// Uses a $type discriminator to determine the concrete event type.
/// </summary>
public sealed class HistoryEventJsonConverter : JsonConverter<HistoryEvent>
{
    public override HistoryEvent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("$type", out var typeProperty))
        {
            throw new JsonException("Missing $type discriminator in HistoryEvent");
        }

        var typeName = typeProperty.GetString();
        var eventType = typeName switch
        {
            "Created" => typeof(VacationRequestCreatedEvent),
            "Exported" => typeof(VacationRequestExportedEvent),
            "Approved" => typeof(VacationRequestApprovedEvent),
            "Rejected" => typeof(VacationRequestRejectedEvent),
            "Archived" => typeof(VacationRequestArchivedEvent),
            "Deleted" => typeof(VacationRequestDeletedEvent),
            _ => throw new JsonException($"Unknown event type: {typeName}")
        };

        var json = root.GetRawText();
        return JsonSerializer.Deserialize(json, eventType, options) as HistoryEvent;
    }

    public override void Write(Utf8JsonWriter writer, HistoryEvent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        // Write discriminator
        var typeName = value switch
        {
            VacationRequestCreatedEvent => "Created",
            VacationRequestExportedEvent => "Exported",
            VacationRequestApprovedEvent => "Approved",
            VacationRequestRejectedEvent => "Rejected",
            VacationRequestArchivedEvent => "Archived",
            VacationRequestDeletedEvent => "Deleted",
            _ => throw new JsonException($"Unknown event type: {value.GetType().Name}")
        };
        writer.WriteString("$type", typeName);

        // Write common properties
        writer.WriteString("EventId", value.EventId);
        writer.WriteString("RequestId", value.RequestId);
        writer.WriteString("Timestamp", value.Timestamp);

        // Write specific properties
        switch (value)
        {
            case VacationRequestCreatedEvent created:
                writer.WriteNumber("Year", created.Year);
                writer.WriteString("StartDate", created.StartDate.ToString("O"));
                writer.WriteString("EndDate", created.EndDate.ToString("O"));
                writer.WriteBoolean("StartHalfDay", created.StartHalfDay);
                writer.WriteBoolean("EndHalfDay", created.EndHalfDay);
                writer.WriteNumber("CalculatedDays", created.CalculatedDays);
                
                if (created.AzaDates != null && created.AzaDates.Count > 0)
                {
                    writer.WritePropertyName("AzaDates");
                    JsonSerializer.Serialize(writer, created.AzaDates, options);
                }
                break;
            case VacationRequestExportedEvent exported:
                writer.WriteString("PdfPath", exported.PdfPath);
                break;
            case VacationRequestRejectedEvent rejected:
                writer.WriteString("RejectionReason", rejected.RejectionReason);
                break;
        }

        writer.WriteEndObject();
    }
}

[JsonSerializable(typeof(HistoryEvent))]
[JsonSerializable(typeof(VacationRequestCreatedEvent))]
[JsonSerializable(typeof(VacationRequestExportedEvent))]
[JsonSerializable(typeof(VacationRequestApprovedEvent))]
[JsonSerializable(typeof(VacationRequestRejectedEvent))]
[JsonSerializable(typeof(VacationRequestArchivedEvent))]
[JsonSerializable(typeof(VacationRequestDeletedEvent))]
[JsonSerializable(typeof(HashSet<DateOnly>))]
internal partial class HistoryJsonContext : JsonSerializerContext
{
}

