using Microsoft.Extensions.Logging;
using Urlaubstool.Domain;

namespace Urlaubstool.Infrastructure.History;

/// <summary>
/// Service interface for managing vacation request history.
/// </summary>
public interface IHistoryService
{
    Task<IReadOnlyList<HistoryEntry>> GetEntriesAsync(int year, StatusFilter filter);
    Task<Guid> CreateAsync(CreateVacationRequestDto dto);
    Task MarkExportedAsync(Guid requestId, string pdfPath);
    Task MarkApprovedAsync(Guid requestId);
    Task MarkRejectedAsync(Guid requestId, string reason);
    Task MarkArchivedAsync(Guid requestId);
    Task MarkDeletedAsync(Guid requestId);
}

/// <summary>
/// Implementation of IHistoryService using event sourcing.
/// Events are stored via IHistoryStore, and projections are built on-the-fly.
/// </summary>
public sealed class HistoryService : IHistoryService
{
    private readonly IHistoryStore _store;
    private readonly ILogger<HistoryService> _logger;

    public HistoryService(IHistoryStore store, ILogger<HistoryService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<IReadOnlyList<HistoryEntry>> GetEntriesAsync(int year, StatusFilter filter)
    {
        var events = await _store.LoadAsync();
        var entries = ProjectEvents(events);

        var filtered = entries.Values
            .Where(e => !e.IsDeleted)
            .Where(e => year == 0 || e.Year == year)  // year=0 means "all years"
            .Where(e => MatchesFilter(e, filter))
            .OrderByDescending(e => e.CreatedAt)
            .ToList();

        _logger.LogInformation("Retrieved {Count} entries for year {Year} with filter {Filter}", 
            filtered.Count, year == 0 ? "ALL" : year.ToString(), filter);

        return filtered;
    }

    public async Task<Guid> CreateAsync(CreateVacationRequestDto dto)
    {
        var requestId = Guid.NewGuid();
        var @event = new VacationRequestCreatedEvent(
            EventId: Guid.NewGuid(),
            RequestId: requestId,
            Timestamp: DateTimeOffset.Now,
            Year: dto.Year,
            StartDate: dto.StartDate,
            EndDate: dto.EndDate,
            StartHalfDay: dto.StartHalfDay,
            EndHalfDay: dto.EndHalfDay,
            CalculatedDays: dto.CalculatedDays,
            AzaDates: dto.AzaDates);

        await _store.AppendAsync(@event);
        _logger.LogInformation("Created request {RequestId} for {Year}", requestId, dto.Year);
        return requestId;
    }

    public async Task MarkExportedAsync(Guid requestId, string pdfPath)
    {
        var @event = new VacationRequestExportedEvent(
            EventId: Guid.NewGuid(),
            RequestId: requestId,
            Timestamp: DateTimeOffset.Now,
            PdfPath: pdfPath);

        await _store.AppendAsync(@event);
        _logger.LogInformation("Marked request {RequestId} as exported: {PdfPath}", requestId, pdfPath);
    }

    public async Task MarkApprovedAsync(Guid requestId)
    {
        var @event = new VacationRequestApprovedEvent(
            EventId: Guid.NewGuid(),
            RequestId: requestId,
            Timestamp: DateTimeOffset.Now);

        await _store.AppendAsync(@event);
        _logger.LogInformation("Marked request {RequestId} as approved", requestId);
    }

    public async Task MarkRejectedAsync(Guid requestId, string reason)
    {
        // Attempt to delete associated PDF before marking rejected
        try
        {
            var events = await _store.LoadAsync();
            var entries = ProjectEvents(events);
            if (entries.TryGetValue(requestId, out var entry) && !string.IsNullOrWhiteSpace(entry.PdfPath))
            {
                try
                {
                    if (System.IO.File.Exists(entry.PdfPath))
                    {
                        System.IO.File.Delete(entry.PdfPath);
                        _logger.LogInformation("Deleted PDF for request {RequestId}: {PdfPath}", requestId, entry.PdfPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete PDF for rejected request {RequestId}: {PdfPath}", requestId, entry.PdfPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inspect history for PDF deletion before rejecting request {RequestId}", requestId);
        }

        var @event = new VacationRequestRejectedEvent(
            EventId: Guid.NewGuid(),
            RequestId: requestId,
            Timestamp: DateTimeOffset.Now,
            RejectionReason: reason);

        await _store.AppendAsync(@event);
        _logger.LogInformation("Marked request {RequestId} as rejected: {Reason}", requestId, reason);
    }

    public async Task MarkArchivedAsync(Guid requestId)
    {
        var @event = new VacationRequestArchivedEvent(
            EventId: Guid.NewGuid(),
            RequestId: requestId,
            Timestamp: DateTimeOffset.Now);

        await _store.AppendAsync(@event);
        _logger.LogInformation("Marked request {RequestId} as archived", requestId);
    }

    public async Task MarkDeletedAsync(Guid requestId)
    {
        // Attempt to delete associated PDF before marking deleted
        try
        {
            var events = await _store.LoadAsync();
            var entries = ProjectEvents(events);
            if (entries.TryGetValue(requestId, out var entry) && !string.IsNullOrWhiteSpace(entry.PdfPath))
            {
                try
                {
                    if (System.IO.File.Exists(entry.PdfPath))
                    {
                        System.IO.File.Delete(entry.PdfPath);
                        _logger.LogInformation("Deleted PDF for request {RequestId}: {PdfPath}", requestId, entry.PdfPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete PDF for deleted request {RequestId}: {PdfPath}", requestId, entry.PdfPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inspect history for PDF deletion before deleting request {RequestId}", requestId);
        }

        var @event = new VacationRequestDeletedEvent(
            EventId: Guid.NewGuid(),
            RequestId: requestId,
            Timestamp: DateTimeOffset.Now);

        await _store.AppendAsync(@event);
        _logger.LogInformation("Marked request {RequestId} as deleted", requestId);
    }

    /// <summary>
    /// Projects events into read model entries.
    /// Each RequestId has one entry built from all its events.
    /// </summary>
    private Dictionary<Guid, HistoryEntry> ProjectEvents(IReadOnlyList<HistoryEvent> events)
    {
        var entries = new Dictionary<Guid, HistoryEntry>();

        foreach (var @event in events)
        {
            if (!entries.TryGetValue(@event.RequestId, out var entry))
            {
                // First event must be Created
                if (@event is not VacationRequestCreatedEvent created)
                {
                    _logger.LogWarning("Skipping non-Created first event for request {RequestId}: {EventType}", 
                        @event.RequestId, @event.GetType().Name);
                    continue;
                }

                entry = new HistoryEntry
                {
                    RequestId = created.RequestId,
                    Year = created.Year,
                    CreatedAt = created.Timestamp,
                    UpdatedAt = created.Timestamp,
                    StartDate = created.StartDate,
                    EndDate = created.EndDate,
                    StartHalfDay = created.StartHalfDay,
                    EndHalfDay = created.EndHalfDay,
                    CalculatedDays = created.CalculatedDays,
                    AzaDates = created.AzaDates,
                    Status = VacationRequestStatus.Draft
                };

                entries[@event.RequestId] = entry;
                continue;
            }

            // Apply subsequent events
            entry = ApplyEvent(entry, @event);
            entries[@event.RequestId] = entry;
        }

        return entries;
    }

    /// <summary>
    /// Applies a single event to an existing entry.
    /// </summary>
    private HistoryEntry ApplyEvent(HistoryEntry entry, HistoryEvent @event)
    {
        return @event switch
        {
            VacationRequestExportedEvent exported => entry with
            {
                UpdatedAt = exported.Timestamp,
                PdfPath = exported.PdfPath,
                Status = VacationRequestStatus.Exported
            },
            VacationRequestApprovedEvent approved => entry with
            {
                UpdatedAt = approved.Timestamp,
                Status = VacationRequestStatus.Approved
            },
            VacationRequestRejectedEvent rejected => entry with
            {
                UpdatedAt = rejected.Timestamp,
                Status = VacationRequestStatus.Rejected,
                RejectionReason = rejected.RejectionReason
            },
            VacationRequestArchivedEvent archived => entry with
            {
                UpdatedAt = archived.Timestamp,
                Status = VacationRequestStatus.Archived,
                ArchivedAt = archived.Timestamp
            },
            VacationRequestDeletedEvent deleted => entry with
            {
                UpdatedAt = deleted.Timestamp,
                Status = VacationRequestStatus.Deleted,
                IsDeleted = true
            },
            _ => entry with { UpdatedAt = @event.Timestamp }
        };
    }

    private static bool MatchesFilter(HistoryEntry entry, StatusFilter filter)
    {
        return filter switch
        {
            StatusFilter.All => true,
            StatusFilter.Exported => entry.Status == VacationRequestStatus.Exported,
            StatusFilter.Approved => entry.Status == VacationRequestStatus.Approved,
            StatusFilter.Rejected => entry.Status == VacationRequestStatus.Rejected,
            StatusFilter.Archived => entry.Status == VacationRequestStatus.Archived,
            _ => true
        };
    }
}
