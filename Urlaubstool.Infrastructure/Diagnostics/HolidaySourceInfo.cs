using System;
using System.Collections.Generic;

namespace Urlaubstool.Infrastructure.Diagnostics;

/// <summary>
/// Represents diagnostic information about a holiday data source (public or school holidays).
/// </summary>
public record HolidaySourceInfo(
    string HolidayType,
    string SourceName,
    string? SourceUrl,
    DateTimeOffset? FetchedAtUtc,
    bool CacheFileExists,
    string? CachePath,
    string? Notes,
    Dictionary<string, List<string>>? CachedDataSummary = null
)
{
    public string FormattedCacheSummary
    {
        get
        {
            if (CachedDataSummary == null || CachedDataSummary.Count == 0)
                return "Keine Daten im Cache.";
            
            var lines = new List<string>();
            foreach (var kvp in CachedDataSummary)
            {
                var states = string.Join(", ", kvp.Value);
                // Truncate if too long?
                if (states.Length > 50) states = states.Substring(0, 47) + "...";
                lines.Add($"{kvp.Key}: {states}");
            }
            return string.Join("\n", lines);
        }
    }
}

