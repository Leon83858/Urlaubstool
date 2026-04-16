namespace Urlaubstool.Domain;

/// <summary>
/// Read model / projection of a vacation request built from history events.
/// This is what the UI displays.
/// </summary>
public sealed record HistoryEntry
{
    public required Guid RequestId { get; init; }
    public required int Year { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }
    public required bool StartHalfDay { get; init; }
    public required bool EndHalfDay { get; init; }
    public required decimal CalculatedDays { get; init; }
    public HashSet<DateOnly>? AzaDates { get; init; }
    public required VacationRequestStatus Status { get; init; }
    public string? RejectionReason { get; init; }
    public string? PdfPath { get; init; }
    public DateTimeOffset? ArchivedAt { get; init; }
    public bool IsDeleted { get; init; }
}

/// <summary>
/// DTO for creating a new vacation request in history.
/// </summary>
public sealed record CreateVacationRequestDto(
    int Year,
    DateOnly StartDate,
    DateOnly EndDate,
    bool StartHalfDay,
    bool EndHalfDay,
    decimal CalculatedDays,
    HashSet<DateOnly>? AzaDates = null);

/// <summary>
/// Filter options for querying history entries.
/// </summary>
public enum StatusFilter
{
    All,
    Exported,
    Approved,
    Rejected,
    Archived
}
