namespace Urlaubstool.Domain;

/// <summary>
/// Base record for all history events. Each event represents an immutable fact.
/// </summary>
public abstract record HistoryEvent(
    Guid EventId,
    Guid RequestId,
    DateTimeOffset Timestamp);

/// <summary>
/// Fired when a vacation request is initially created.
/// </summary>
public sealed record VacationRequestCreatedEvent(
    Guid EventId,
    Guid RequestId,
    DateTimeOffset Timestamp,
    int Year,
    DateOnly StartDate,
    DateOnly EndDate,
    bool StartHalfDay,
    bool EndHalfDay,
    decimal CalculatedDays,
    HashSet<DateOnly>? AzaDates = null) : HistoryEvent(EventId, RequestId, Timestamp);

/// <summary>
/// Fired when a request is exported to PDF.
/// </summary>
public sealed record VacationRequestExportedEvent(
    Guid EventId,
    Guid RequestId,
    DateTimeOffset Timestamp,
    string PdfPath) : HistoryEvent(EventId, RequestId, Timestamp);

/// <summary>
/// Fired when a request is approved by supervisor.
/// </summary>
public sealed record VacationRequestApprovedEvent(
    Guid EventId,
    Guid RequestId,
    DateTimeOffset Timestamp) : HistoryEvent(EventId, RequestId, Timestamp);

/// <summary>
/// Fired when a request is rejected by supervisor.
/// </summary>
public sealed record VacationRequestRejectedEvent(
    Guid EventId,
    Guid RequestId,
    DateTimeOffset Timestamp,
    string RejectionReason) : HistoryEvent(EventId, RequestId, Timestamp);

/// <summary>
/// Fired when a request is archived (soft removal from active view).
/// </summary>
public sealed record VacationRequestArchivedEvent(
    Guid EventId,
    Guid RequestId,
    DateTimeOffset Timestamp) : HistoryEvent(EventId, RequestId, Timestamp);

/// <summary>
/// Fired when a request is soft-deleted (permanent removal from history view).
/// </summary>
public sealed record VacationRequestDeletedEvent(
    Guid EventId,
    Guid RequestId,
    DateTimeOffset Timestamp) : HistoryEvent(EventId, RequestId, Timestamp);
