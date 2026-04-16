using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Urlaubstool.Domain;

namespace Urlaubstool.Infrastructure.Holidays;

/// <summary>
/// Offline school holiday provider backed by embedded JSON data. Splits cross-year ranges into per-year segments to ensure January dates resolve.
/// </summary>
public sealed class SchoolHolidayProvider : ISchoolHolidayProvider
{
    private IReadOnlyDictionary<string, IReadOnlyDictionary<int, List<DateRange>>> _data;

    private record JsonRange(DateOnly start, DateOnly end);
    private record JsonYear(IDictionary<string, JsonRange[]> Years);

    public SchoolHolidayProvider()
    {
        _data = Load(null);
    }

    public void Reload(string? cachePath)
    {
        _data = Load(cachePath);
    }

    public bool IsSchoolHoliday(DateOnly date, string state)
    {
        var key = state.ToUpperInvariant();
        if (key == "NRW")
        {
            key = "NW";
        }

        if (!_data.TryGetValue(key, out var perYear))
        {
            return false;
        }

        // Check same year.
        if (perYear.TryGetValue(date.Year, out var ranges))
        {
            if (ranges.Any(r => r.Contains(date)))
            {
                return true;
            }
        }

        // Defensive: check previous year in case an unforeseen cross-year segment spans into January and was not split.
        if (perYear.TryGetValue(date.Year - 1, out var prevRanges))
        {
            if (prevRanges.Any(r => r.Contains(date)))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<int, List<DateRange>>> Load(string? cachePath)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<int, List<DateRange>>>(StringComparer.OrdinalIgnoreCase);

        // 1. Load embedded
        try 
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("Urlaubstool.Infrastructure.Data.school_holidays.json");
            if (stream != null)
            {
                MergeJson(stream, result);
            }
        }
        catch { /* Ignore embedded load errors */ }

        // 2. Load cache
        if (!string.IsNullOrEmpty(cachePath) && File.Exists(cachePath))
        {
            try
            {
                using var stream = File.OpenRead(cachePath);
                MergeJson(stream, result);
            }
            catch { /* Ignore cache load errors */ }
        }

        return result;
    }

    private static void MergeJson(Stream stream, Dictionary<string, IReadOnlyDictionary<int, List<DateRange>>> result)
    {
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        foreach (var stateProperty in root.EnumerateObject())
        {
            var stateKey = stateProperty.Name.ToUpperInvariant();
            
            // Build mutable dictionary for this state
            Dictionary<int, List<DateRange>> perYear;
            if (result.TryGetValue(stateKey, out var existingReadOnly))
            {
                // Clone existing readonly dictionary to mutable
                perYear = existingReadOnly.ToDictionary(k => k.Key, v => new List<DateRange>(v.Value));
            }
            else
            {
                perYear = new Dictionary<int, List<DateRange>>();
            }

            foreach (var yearProperty in stateProperty.Value.EnumerateObject())
            {
                if (!int.TryParse(yearProperty.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var year))
                {
                    continue; // Skip invalid keys
                }

                // If loading from cache, we might overwrite/merge existing year. 
                // For simplicity: Cache wins (overwrites) or adds to existing year list?
                // Holidays are usually static sets. If we receive data for a year, it's likely the full set.
                // However, standard merging might be safer if we partial updates.
                // But let's just append for now, assuming sources don't overlap or we don't care about duplicates (Contains check handles it).
                
                // EDIT: Overwrite year list is cleaner if we assume the source has the "truth" for that year.
                // But "school_holidays.json" has partials? No, it has full lists per year.
                // So if we find the year in the new source, let's Replace the list for that year?
                // Actually, the structure of "school_holidays.json" is { "NW": { "2024": [...] } }.
                // If we load cache second, and it has "NW": { "2024": [ ... ] }, it should overwrite the embedded one?
                // Yes, cache should be more recent.
                
                // However, our code loops through ranges.
                // Let's clear the list for that year if we encounter it in the new source?
                // No, because we might process multiple sources.
                // Since this method is called sequentially:
                // 1. Load(embedded) -> fills dictionary.
                // 2. Load(cache) -> calls MergeJson on the RESULT of 1.
                // So if cache has "2024", we probably want to use the cache's version.
                
                // Strategy: If the year exists in the incoming stream, we should probably start with a fresh list for that year *from this stream*,
                // but wait, we are modifying 'perYear'.
                // Ideally: if (incoming source has year X), use that list.
                // But we act iteratively.
                
                // Let's reset the list for this year ONLY if it's the first time we see this year *in this specific stream*.
                // But we don't know that easily here.
                
                // Simple approach: parse all ranges from this stream into a local structure first, then merge into result.
            }
        }
        
        // Let's implement robust merge:
        // Parse stream fully into a localized dictionary
        var incomingData = ParseStream(doc.RootElement);
        
        foreach (var stateKvp in incomingData)
        {
            var state = stateKvp.Key;
            var incomingYears = stateKvp.Value;
            
            if (!result.TryGetValue(state, out var existingYears))
            {
                 result[state] = incomingYears;
                 continue;
            }
            
            // Merge years. Incoming overwrites existing for same year.
            var mutableYears = existingYears.ToDictionary(k => k.Key, v => v.Value);
            foreach (var yearKvp in incomingYears)
            {
                mutableYears[yearKvp.Key] = yearKvp.Value;
            }
            result[state] = mutableYears;
        }
    }

    private static Dictionary<string, IReadOnlyDictionary<int, List<DateRange>>> ParseStream(JsonElement root)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<int, List<DateRange>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var stateProperty in root.EnumerateObject())
        {
            var perYear = new Dictionary<int, List<DateRange>>();
            foreach (var yearProperty in stateProperty.Value.EnumerateObject())
            {
                if (!int.TryParse(yearProperty.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var year)) continue;
                
                foreach (var rangeElement in yearProperty.Value.EnumerateArray())
                {
                    if (!rangeElement.TryGetProperty("start", out var startProp) || !rangeElement.TryGetProperty("end", out var endProp)) continue;
                    
                    if (!DateOnly.TryParse(startProp.GetString(), CultureInfo.InvariantCulture, out var start) ||
                        !DateOnly.TryParse(endProp.GetString(), CultureInfo.InvariantCulture, out var end)) continue;

                    foreach (var split in SplitCrossYear(start, end))
                    {
                        if (!perYear.TryGetValue(split.Start.Year, out var list))
                        {
                            list = new List<DateRange>();
                            perYear[split.Start.Year] = list;
                        }
                        list.Add(split);
                    }
                }
            }
            result[stateProperty.Name.ToUpperInvariant()] = perYear.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
        return result;
    }

    private static IEnumerable<DateRange> SplitCrossYear(DateOnly start, DateOnly end)
    {
        if (start.Year == end.Year)
        {
            yield return new DateRange(start, end);
            yield break;
        }

        var currentStart = start;
        var currentEnd = new DateOnly(start.Year, 12, 31);
        yield return new DateRange(currentStart, currentEnd);

        var nextStart = new DateOnly(start.Year + 1, 1, 1);
        var remainingEnd = end;
        if (nextStart.Year == remainingEnd.Year)
        {
            yield return new DateRange(nextStart, remainingEnd);
            yield break;
        }

        // In practice ranges will not span multiple years, but handle generically.
        var iterStart = nextStart;
        while (iterStart.Year < remainingEnd.Year)
        {
            var yearEnd = new DateOnly(iterStart.Year, 12, 31);
            yield return new DateRange(iterStart, yearEnd);
            iterStart = new DateOnly(iterStart.Year + 1, 1, 1);
        }
        yield return new DateRange(iterStart, remainingEnd);
    }

    private sealed record DateRange(DateOnly Start, DateOnly End)
    {
        public bool Contains(DateOnly date) => date >= Start && date <= End;
    }
}
