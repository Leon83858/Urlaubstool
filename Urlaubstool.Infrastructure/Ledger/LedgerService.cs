using System.Globalization;
using System.Threading;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.Paths;

namespace Urlaubstool.Infrastructure.Ledger;

/// <summary>
/// Manages the ledger CSV with schema versioning and safe writes.
/// </summary>
public sealed class LedgerService
{
    public const int SchemaVersion = 1;
    private readonly PathService _paths;

    public LedgerService(PathService paths)
    {
        _paths = paths;
    }

    public LedgerSnapshot Load()
    {
        var path = _paths.GetLedgerFilePath();
        if (!File.Exists(path))
        {
            return new LedgerSnapshot(Array.Empty<LedgerEntry>());
        }

        var lines = File.ReadAllLines(path);
        var entries = new List<LedgerEntry>();
        foreach (var line in lines.Skip(1)) // skip header
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var cells = ParseCsvLine(line);
            if (cells.Length < 13)
            {
                continue;
            }

            try
            {
                var entry = new LedgerEntry(
                    int.Parse(cells[0], CultureInfo.InvariantCulture),
                    int.Parse(cells[1], CultureInfo.InvariantCulture),
                    Guid.Parse(cells[2]),
                    DateTimeOffset.Parse(cells[3], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    DateOnly.Parse(cells[4], CultureInfo.InvariantCulture),
                    DateOnly.Parse(cells[5], CultureInfo.InvariantCulture),
                    bool.Parse(cells[6]),
                    bool.Parse(cells[7]),
                    decimal.Parse(cells[8], CultureInfo.InvariantCulture),
                    Enum.Parse<VacationRequestStatus>(cells[9]),
                    string.IsNullOrWhiteSpace(cells[10]) ? null : cells[10],
                    string.IsNullOrWhiteSpace(cells[11]) ? null : DateTimeOffset.Parse(cells[11], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    string.IsNullOrWhiteSpace(cells[12]) ? null : cells[12]
                );
                
                // MIGRATION: Fix old data where PdfPath was written to RejectionReason field
                if (string.IsNullOrWhiteSpace(entry.PdfPath) && 
                    !string.IsNullOrWhiteSpace(entry.RejectionReason) && 
                    entry.RejectionReason.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    entry = entry with { PdfPath = entry.RejectionReason, RejectionReason = null };
                    System.Diagnostics.Debug.WriteLine($"[LedgerService.Load] Migrated legacy entry: Moved '{entry.PdfPath}' from RejectionReason to PdfPath");
                }
                
                entries.Add(entry);
            }
            catch (Exception ex)
            {
                // Skip malformed rows to keep the ledger resilient.
                System.Diagnostics.Debug.WriteLine($"[LedgerService.Load] Failed to parse row: {ex.Message}\nCells: {string.Join(",", cells)}");
            }
        }

        return new LedgerSnapshot(entries);
    }

    public void Save(IEnumerable<LedgerEntry> entries)
    {
        var path = _paths.GetLedgerFilePath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var lines = new List<string>
        {
            "SchemaVersion,Year,RequestId,CreatedAt,StartDate,EndDate,StartHalfDay,EndHalfDay,DaysRequested,Status,RejectionReason,ArchivedAt,PdfPath"
        };

        foreach (var e in entries)
        {
            lines.Add(string.Join(',',
                e.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                e.Year.ToString(CultureInfo.InvariantCulture),
                Escape(e.RequestId.ToString()),
                Escape(e.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
                Escape(e.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                Escape(e.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                e.StartHalfDay.ToString(CultureInfo.InvariantCulture),
                e.EndHalfDay.ToString(CultureInfo.InvariantCulture),
                e.DaysRequested.ToString(CultureInfo.InvariantCulture),
                Escape(e.Status.ToString()),
                Escape(e.RejectionReason ?? string.Empty),
                Escape(e.ArchivedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
                Escape(e.PdfPath ?? string.Empty)));
        }

        var temp = Path.Combine(directory, $"ledger_{Guid.NewGuid():N}.tmp");
        File.WriteAllLines(temp, lines);

        PersistWithRetry(temp, path);
    }

    private static string Escape(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    private static string[] ParseCsvLine(string line)
    {
        var cells = new List<string>();
        var current = new List<char>();
        var insideQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (insideQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Add('"');
                    i++;
                }
                else
                {
                    insideQuotes = !insideQuotes;
                }
            }
            else if (c == ',' && !insideQuotes)
            {
                cells.Add(new string(current.ToArray()));
                current.Clear();
            }
            else
            {
                current.Add(c);
            }
        }
        cells.Add(new string(current.ToArray()));
        return cells.ToArray();
    }

    private static void PersistWithRetry(string tempPath, string finalPath)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }
                File.Move(tempPath, finalPath);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(50);
            }
        }

        // If all attempts fail, throw to surface the issue.
        File.Move(tempPath, finalPath);
    }
}
