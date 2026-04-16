using System.Text.Json;
using Urlaubstool.Domain;

namespace Urlaubstool.Infrastructure.Holidays;

/// <summary>
/// Hybrid public holiday provider that uses cached online data if available, 
/// with a fallback to the offline provider logic.
/// Ensures baseline fixed nationwide holidays are always respected.
/// </summary>
public sealed class HybridPublicHolidayProvider : IPublicHolidayProvider
{
    private readonly PublicHolidayProvider _local = new();
    private readonly object _lock = new();
    private Dictionary<int, Dictionary<string, HashSet<DateOnly>>> _cache = new();
    
    // Mapping from explicit short codes (NW) to ISO codes (DE-NW) is typically needed for fetching, 
    // but the cache is stored with short codes (or whatever OnlinePublicHolidayService produces).
    // Assuming the cache stores keys as "NW", "BY", etc.
    
    public void Reload(string? cachePath)
    {
        lock (_lock)
        {
            _cache.Clear();
            if (string.IsNullOrEmpty(cachePath) || !File.Exists(cachePath))
            {
                return;
            }

            try
            {
                using var stream = File.OpenRead(cachePath);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                
                // Handle optional meta wrapper
                JsonElement dataRoot = root;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataProp))
                {
                    dataRoot = dataProp;
                }

                if (dataRoot.ValueKind != JsonValueKind.Object) return;

                foreach (var yearProp in dataRoot.EnumerateObject())
                {
                    if (!int.TryParse(yearProp.Name, out var year)) continue;
                    
                    var stateDict = new Dictionary<string, HashSet<DateOnly>>(StringComparer.OrdinalIgnoreCase);
                    
                    foreach (var stateProp in yearProp.Value.EnumerateObject())
                    {
                        var stateCode = stateProp.Name;
                        var dates = new HashSet<DateOnly>();
                        
                        foreach (var dateEl in stateProp.Value.EnumerateArray())
                        {
                            if (DateOnly.TryParseExact(dateEl.GetString(), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d))
                            {
                                dates.Add(d);
                            }
                        }
                        stateDict[stateCode] = dates;
                    }
                    _cache[year] = stateDict;
                }
            }
            catch
            {
                // Verify minimal stability, ignore corrupted cache
            }
        }
    }

    public bool IsPublicHoliday(DateOnly date, string state)
    {
        var normalizedState = state.Trim().ToUpperInvariant();
        if (normalizedState == "NRW") normalizedState = "NW";

        bool cacheHit = false;
        bool isHolidayInCache = false;

        lock (_lock)
        {
            if (_cache.TryGetValue(date.Year, out var stateDict))
            {
                if (stateDict.TryGetValue(normalizedState, out var dates))
                {
                    cacheHit = true;
                    if (dates.Contains(date))
                    {
                        isHolidayInCache = true;
                    }
                }
            }
        }

        if (cacheHit)
        {
            // If cache says true, it's true.
            // If cache says false, we must still respect hard baseline holidays in case cache is weirdly incomplete 
            // but effectively valid (though fetch logic should prevent that).
            return isHolidayInCache || IsHardBaselineHoliday(date);
        }

        // Fallback to local offline logic
        return _local.IsPublicHoliday(date, state);
    }

    private static bool IsHardBaselineHoliday(DateOnly date)
    {
        return (date.Month, date.Day) switch
        {
            (1, 1) => true,   // Neujahr
            (5, 1) => true,   // Tag der Arbeit
            (10, 3) => true,  // Tag der Deutschen Einheit
            (12, 25) => true, // 1. Weihnachtsfeiertag
            (12, 26) => true, // 2. Weihnachtsfeiertag
            _ => false
        };
    }
}
