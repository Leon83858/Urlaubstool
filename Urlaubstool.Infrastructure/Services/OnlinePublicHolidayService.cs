using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Urlaubstool.Domain;

namespace Urlaubstool.Infrastructure.Services;

public sealed class OnlinePublicHolidayService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OnlinePublicHolidayService>? _logger;

    // Default basic constructor
    public OnlinePublicHolidayService(ILogger<OnlinePublicHolidayService>? logger = null)
    {
        _httpClient = new HttpClient();
        // Add User-Agent as good practice
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Urlaubstool/1.0");
        _logger = logger;
    }

    private record NagerHolidayDto(
        string Date,
        string LocalName,
        string Name,
        string CountryCode,
        bool Fixed,
        bool Global,
        string[]? Counties,
        int? LaunchYear,
        string[]? Types
    );

    private record CacheRoot(
        [property: JsonPropertyName("meta")] CacheMeta Meta, 
        [property: JsonPropertyName("data")] Dictionary<string, Dictionary<string, List<string>>> Data
    );

    private record CacheMeta(
        [property: JsonPropertyName("version")] int SchemaVersion, 
        [property: JsonPropertyName("source")] string Source, 
        [property: JsonPropertyName("sourceUrl")] string? SourceUrl, 
        [property: JsonPropertyName("country")] string Country, 
        [property: JsonPropertyName("fetchedAtUtc")] DateTimeOffset FetchedAtUtc
    );

    public async Task<bool> FetchAndCacheAsync(int year, string cachePath)
    {
        try
        {
            var url = $"https://date.nager.at/api/v3/PublicHolidays/{year}/DE";
            var holidays = await _httpClient.GetFromJsonAsync<NagerHolidayDto[]>(url);

            if (holidays == null || holidays.Length == 0)
            {
                _logger?.LogWarning("Nager.Date returned no holidays for year {Year}", year);
                return false;
            }

            // Processing
            // States map: ShortCode -> Set of Dates
            var states = Bundeslaender.Codes.ToDictionary(c => c, _ => new HashSet<DateOnly>());

            foreach (var h in holidays)
            {
                // Must be Public
                if (h.Types != null && !h.Types.Contains("Public"))
                {
                    continue; // Skip non-public (school, bank, observance, etc.) if API returns them
                }
                
                if (!DateOnly.TryParseExact(h.Date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date))
                {
                    continue;
                }
                
                // If global, apply to all states
                if (h.Global)
                {
                    foreach (var state in states.Keys)
                    {
                        states[state].Add(date);
                    }
                }
                else if (h.Counties != null)
                {
                    foreach (var county in h.Counties)
                    {
                        // county format: DE-NW, DE-BY, etc.
                        var shortState = county.Replace("DE-", "").ToUpperInvariant();
                        
                        if (states.TryGetValue(shortState, out var set))
                        {
                            set.Add(date);
                        }
                    }
                }
            }

            // Validation
            foreach (var kvp in states)
            {
                var code = kvp.Key;
                var dates = kvp.Value;

                // 1. Check count (conservative minimum 8)
                if (dates.Count < 8)
                {
                    _logger?.LogWarning("Validation failed for state {State} in {Year}: only {Count} holidays found (expected >= 8). Aborting cache update.", code, year, dates.Count);
                    return false;
                }

                // 2. Check baseline compliance
                if (!CheckBaseline(dates, year))
                {
                    _logger?.LogWarning("Validation failed for state {State} in {Year}: missing hard baseline holidays. Aborting cache update.", code, year);
                    return false;
                }
            }

            // Merge with existing cache file if present
            Dictionary<string, Dictionary<string, List<string>>> fullData;
            
            if (File.Exists(cachePath))
            {
                try
                {
                    var existingContent = await File.ReadAllBytesAsync(cachePath);
                    var existingDoc = JsonDocument.Parse(existingContent);
                    var existingRoot = existingDoc.RootElement;
                    
                    if (existingRoot.TryGetProperty("data", out var oldDataProp))
                    {
                         fullData = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<string>>>>(oldDataProp) 
                                    ?? new Dictionary<string, Dictionary<string, List<string>>>();
                    }
                    else
                    {
                        // legacy or invalid -> start fresh
                        fullData = new Dictionary<string, Dictionary<string, List<string>>>();
                    }
                }
                catch
                {
                    fullData = new Dictionary<string, Dictionary<string, List<string>>>();
                }
            }
            else
            {
                fullData = new Dictionary<string, Dictionary<string, List<string>>>();
            }

            // Convert current year states to string lists for JSON
            var yearString = year.ToString();
            var statesForJson = states.ToDictionary(
                k => k.Key, 
                v => v.Value.OrderBy(d => d).Select(d => d.ToString("yyyy-MM-dd")).ToList()
            );

            fullData[yearString] = statesForJson;

            // Construct full object
            var rootObj = new CacheRoot(
                new CacheMeta(1, "Nager.Date", "https://date.nager.at", "DE", DateTimeOffset.UtcNow),
                fullData
            );

            var dir = Path.GetDirectoryName(cachePath);
            if (dir != null) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(rootObj, new JsonSerializerOptions { WriteIndented = true });
            
            // Atomic write
            var tempPath = cachePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json);
            
            if (File.Exists(cachePath)) File.Delete(cachePath);
            File.Move(tempPath, cachePath);
            
            _logger?.LogInformation("Successfully updated public holiday cache for year {Year}", year);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error fetching public holidays for {Year}", year);
            return false;
        }
    }

    private static bool CheckBaseline(HashSet<DateOnly> dates, int year)
    {
        // Must contain: 01-01, 05-01, 10-03, 12-25, 12-26
        var baseline = new[]
        {
            new DateOnly(year, 1, 1),
            new DateOnly(year, 5, 1),
            new DateOnly(year, 10, 3),
            new DateOnly(year, 12, 25),
            new DateOnly(year, 12, 26)
        };

        foreach (var b in baseline)
        {
            if (!dates.Contains(b)) return false;
        }
        return true;
    }
}
