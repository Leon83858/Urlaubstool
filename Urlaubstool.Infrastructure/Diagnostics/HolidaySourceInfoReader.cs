using System.Text.Json;

namespace Urlaubstool.Infrastructure.Diagnostics;

/// <summary>
/// Reads metadata from holiday cache files to provide diagnostic information about data sources.
/// </summary>
public static class HolidaySourceInfoReader
{
    public static HolidaySourceInfo GetPublicHolidayInfo(string cachePath)
    {
        return ReadPublicHolidayInfoInternal("Public Holidays", cachePath, "Generic fallback (offline rules).");
    }

    public static HolidaySourceInfo GetSchoolHolidayInfo(string cachePath)
    {
        return ReadSchoolHolidayInfoInternal("School Holidays", cachePath, "Using embedded offline data.");
    }

    private static HolidaySourceInfo ReadPublicHolidayInfoInternal(string type, string cachePath, string fallbackNote)
    {
        if (string.IsNullOrEmpty(cachePath) || !File.Exists(cachePath))
        {
            return new HolidaySourceInfo(
                type,
                "Offline / Embedded",
                null,
                null,
                false,
                cachePath,
                fallbackNote
            );
        }

        try
        {
            using var stream = File.OpenRead(cachePath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            
            // Public Holidays Structure: { meta: {...}, data: { YEAR: { STATE: [...] } } }

            string source = "Unknown (cache)";
            string? url = null;
            DateTimeOffset? fetchedAt = null;
            string notes = "Loaded from cache.";

            if (root.TryGetProperty("meta", out var meta))
            {
                source = GetString(meta, "source") ?? source;
                url = GetString(meta, "sourceUrl");
                var fetchedAtStr = GetString(meta, "fetchedAtUtc");
                if (DateTimeOffset.TryParse(fetchedAtStr, out var fa))
                {
                    fetchedAt = fa;
                }
            }
            else
            {
                source = "Cache data (legacy/no meta)";
                notes = "Loaded from cache. (legacy format)";
            }

            Dictionary<string, List<string>>? summary = null;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                summary = new Dictionary<string, List<string>>();
                foreach (var yearProp in data.EnumerateObject())
                {
                    var year = yearProp.Name;
                    var states = new List<string>();
                    if (yearProp.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var stateProp in yearProp.Value.EnumerateObject())
                        {
                            states.Add(stateProp.Name);
                        }
                    }
                    summary[year] = states;
                }
            }

            return new HolidaySourceInfo(
                type,
                source,
                url,
                fetchedAt,
                true,
                cachePath,
                notes,
                summary
            );
        }
        catch (Exception ex)
        {
            return new HolidaySourceInfo(
                type,
                "Fehler beim Lesen des Cache",
                null,
                null,
                true,
                cachePath,
                $"Datei existiert, ist aber ungültig: {ex.Message}"
            );
        }
    }


    private static HolidaySourceInfo ReadSchoolHolidayInfoInternal(string type, string cachePath, string fallbackNote)
    {
        if (string.IsNullOrEmpty(cachePath) || !File.Exists(cachePath))
        {
            return new HolidaySourceInfo(
                type,
                "Offline / Embedded",
                null,
                null,
                false,
                cachePath,
                fallbackNote
            );
        }

        try
        {
            using var stream = File.OpenRead(cachePath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            
            // School Holidays Structure: { STATE: { YEAR: [...] }, ... }
            // Or wrapped: { data: { STATE: { YEAR: [...] } } } (supported by provider, but currently Service writes plain)
            // No meta is currently written by OnlineHolidayService.

            JsonElement dataRoot = root;
            if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                dataRoot = d;
            }

            var summary = new Dictionary<string, List<string>>();
            
            // Iterate States first
            foreach (var stateProp in dataRoot.EnumerateObject())
            {
                // Skip 'meta' if it exists at root level and we are looking at root
                if (stateProp.NameEquals("meta") && dataRoot.Equals(root)) continue;

                var state = stateProp.Name;
                if (stateProp.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var yearProp in stateProp.Value.EnumerateObject())
                    {
                        var year = yearProp.Name;
                        if (!summary.ContainsKey(year))
                        {
                            summary[year] = new List<string>();
                        }
                        if (!summary[year].Contains(state))
                        {
                            summary[year].Add(state);
                        }
                    }
                }
            }

            // Detect meta if possible
            string source = "Cache data (Legacy/no meta)";
            string? url = null;
            DateTimeOffset? fetchedAt = null;
            string notes = "Loaded from cache.";

            if (root.TryGetProperty("meta", out var meta))
            {
                source = GetString(meta, "source") ?? source;
                url = GetString(meta, "sourceUrl");
                var fetchedAtStr = GetString(meta, "fetchedAtUtc");
                if (DateTimeOffset.TryParse(fetchedAtStr, out var fa))
                {
                    fetchedAt = fa;
                }
            }
            else
            {
                // Legacy/no-meta shape: add a hint in notes
                notes = "Loaded from cache. (legacy format)";
                // Infer from file time if valid
                try { fetchedAt = File.GetLastWriteTimeUtc(cachePath); } catch { }
            }

            return new HolidaySourceInfo(
                type,
                source,
                url,
                fetchedAt,
                true,
                cachePath,
                notes,
                summary
            );
        }
        catch (Exception ex)
        {
            return new HolidaySourceInfo(
                type,
                "Error reading cache",
                null,
                null,
                true,
                cachePath,
                $"File exists but is invalid: {ex.Message}"
            );
        }
    }


    private static string? GetString(JsonElement el, string propName)
    {
        if (el.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }
}
