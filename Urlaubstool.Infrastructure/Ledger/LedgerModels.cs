using Urlaubstool.Domain;

namespace Urlaubstool.Infrastructure.Ledger;

public sealed record LedgerEntry(
    int SchemaVersion,
    int Year,
    Guid RequestId,
    DateTimeOffset CreatedAt,
    DateOnly StartDate,
    DateOnly EndDate,
    bool StartHalfDay,
    bool EndHalfDay,
    decimal DaysRequested,
    VacationRequestStatus Status,
    string? RejectionReason,
    DateTimeOffset? ArchivedAt,
    string? PdfPath);

public sealed record LedgerSnapshot(IReadOnlyList<LedgerEntry> Entries)
{
    public decimal GetApprovedTotal(int year) => Entries.Where(e => e.Year == year && e.Status == VacationRequestStatus.Approved).Sum(e => e.DaysRequested);
}
