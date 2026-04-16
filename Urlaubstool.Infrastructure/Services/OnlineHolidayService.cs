using System.Text.Json;
using System.Text.Json.Nodes;

namespace Urlaubstool.Infrastructure.Services;

public class OnlineHolidayService
{
    private readonly HttpClient _httpClient;

    public OnlineHolidayService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Urlaubstool/1.0");
    }

    public async Task<bool> FetchAndCacheAsync(string state, int year, string cachePath)
    {
        try
        {
            // API: https://ferien-api.de/api/v1/holidays/{stateCode}/{year}
            // State needs to be correct ISO code. Our app uses "NW", "BY", etc.
            // ferien-api uses "NW", "BY", etc. (ISO 3166-2:DE without DE-)
            
            // Map NRW -> NW handles in calling code, but ensuring here:
            var apiState = state == "NRW" ? "NW" : state;

            var url = $"https://ferien-api.de/api/v1/holidays/{apiState}/{year}";
            
            // Allow fetch to fail gracefully
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var content = await response.Content.ReadAsStringAsync();
            var holidays = JsonNode.Parse(content)?.AsArray();

            if (holidays == null) return false;

            // Format for cache file: { "STATE": { "YEAR": [ { "start": "...", "end": "..." } ] } }
            // We need to Read existing cache, Update it, Write it back.

            JsonObject rootCache;
            if (File.Exists(cachePath))
            {
                try
                {
                    var existingContent = await File.ReadAllTextAsync(cachePath);
                    rootCache = JsonNode.Parse(existingContent)?.AsObject() ?? new JsonObject();
                }
                catch
                {
                    rootCache = new JsonObject();
                }
            }
            else
            {
                rootCache = new JsonObject();
            }

            // Ensure State object exists
            if (!rootCache.ContainsKey(apiState))
            {
                rootCache[apiState] = new JsonObject();
            }
            var stateObj = rootCache[apiState]!.AsObject();

            // Create list of ranges
            var rangeArray = new JsonArray();
            foreach (var holiday in holidays)
            {
                var start = holiday["start"]?.ToString().Substring(0, 10); // "YYYY-MM-DD..."
                var end = holiday["end"]?.ToString().Substring(0, 10);

                if (start != null && end != null)
                {
                    rangeArray.Add(new JsonObject
                    {
                        ["start"] = start,
                        ["end"] = end
                    });
                }
            }

            // Update Year
            stateObj[year.ToString()] = rangeArray;

            // Save back
            var options = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(cachePath, rootCache.ToJsonString(options));

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
