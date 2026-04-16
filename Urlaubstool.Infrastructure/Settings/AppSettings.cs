using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.Paths;

namespace Urlaubstool.Infrastructure.Settings;

/// <summary>
/// JSON Source Generator Context for AOT-compatible serialization.
/// Provides type information to the JSON serializer without reflection.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Application settings persisted to JSON with schema versioning and safe writes.
/// Includes both employee/student info and vacation calculation fields required for Excel template export.
/// </summary>
public sealed class AppSettings
{
    public const int SchemaVersion = 2; // Bumped for template fields

    public int Version { get; init; } = SchemaVersion;
    
    // Personal information (Excel template placeholders)
    public string Name { get; init; } = string.Empty; // Full name (computed for backward compatibility)
    public string Vorname { get; init; } = string.Empty; // First name
    public string Nachname { get; init; } = string.Empty; // Last name
    public string Adresse { get; init; } = string.Empty; // Address
    public string Abteilung { get; init; } = string.Empty;
    public string Personalnummer { get; init; } = string.Empty; // Personnel number (optional)
    
    // Export settings
    public string? ExportPath { get; init; } = null; // Custom export path (null = use default)
    
    // Existing vacation config
    public string Klasse { get; init; } = string.Empty;
    public decimal Jahresurlaub { get; init; } = 0m;
    public HashSet<DayOfWeek> Workdays { get; init; } = new();
    public bool StudentActive { get; init; }
    public string? Bundesland { get; init; }
    public Dictionary<DayOfWeek, VocationalSchoolDayType> VocationalSchool { get; init; } = new();

    public static AppSettings CreateDefault() => new()
    {
        Name = string.Empty,
        Vorname = string.Empty,
        Nachname = string.Empty,
        Adresse = string.Empty,
        Abteilung = string.Empty,
        Personalnummer = string.Empty,
        ExportPath = null,
        Klasse = string.Empty,
        Jahresurlaub = 0m,
        Workdays = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
        StudentActive = false,
        Bundesland = null,
        VocationalSchool = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => VocationalSchoolDayType.None)
    };
}

public sealed class SettingsService
{
    private readonly PathService _paths;
    private readonly JsonSerializerOptions _jsonOptions;

    public SettingsService(PathService paths)
    {
        _paths = paths;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            TypeInfoResolver = AppSettingsJsonContext.Default
        };
    }

    public async Task<AppSettings?> LoadAsync()
    {
        var path = _paths.GetSettingsFilePath();
        if (!File.Exists(path))
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService.LoadAsync] Settings file not found: {path}");
            return null;
        }

        System.Diagnostics.Debug.WriteLine($"[SettingsService.LoadAsync] Loading settings from: {path}");
        await using var stream = File.OpenRead(path);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _jsonOptions);
        
        // Apply backward compatibility migration: convert legacy "Name" field to Vorname+Nachname
        if (settings != null)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService.LoadAsync] Before migration - Vorname: '{settings.Vorname}', Nachname: '{settings.Nachname}', Name: '{settings.Name}'");
            settings = MigrateLegacyNameField(settings);
            System.Diagnostics.Debug.WriteLine($"[SettingsService.LoadAsync] After migration - Vorname: '{settings.Vorname}', Nachname: '{settings.Nachname}'");
        }
        
        return settings;
    }

    /// <summary>
    /// Migrates legacy settings where only "Name" field exists (full name) to separate Vorname+Nachname.
    /// If Nachname is empty but Name is populated, splits Name and updates both fields.
    /// Migration is only applied if fields are empty and Name is populated.
    /// Returns a new AppSettings instance with migrated values.
    /// </summary>
    private static AppSettings MigrateLegacyNameField(AppSettings settings)
    {
        // Only migrate if Nachname is empty but Name has content
        if (!string.IsNullOrWhiteSpace(settings.Nachname) || string.IsNullOrWhiteSpace(settings.Name))
        {
            return settings;
        }

        // Split Name into Vorname and Nachname
        // Strategy: Last space-separated word becomes Nachname, rest becomes Vorname
        var parts = settings.Name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        
        string migratedVorname = string.Empty;
        string migratedNachname = string.Empty;

        if (parts.Length == 0)
        {
            // Edge case: only whitespace - use Name as-is for Nachname
            migratedNachname = settings.Name.Trim();
        }
        else if (parts.Length == 1)
        {
            // Single word: treat as Nachname (conservative approach)
            migratedNachname = parts[0];
            migratedVorname = string.Empty;
        }
        else
        {
            // Multiple words: last word is Nachname, rest is Vorname
            migratedNachname = parts[parts.Length - 1];
            migratedVorname = string.Join(" ", parts.Take(parts.Length - 1));
        }

        // Return a new AppSettings with migrated Vorname/Nachname
        // Use record-style pattern: create new instance with updated values
        return new AppSettings
        {
            Version = settings.Version,
            Name = settings.Name,
            Vorname = !string.IsNullOrWhiteSpace(settings.Vorname) ? settings.Vorname : migratedVorname,
            Nachname = migratedNachname,
            Adresse = settings.Adresse,
            Abteilung = settings.Abteilung,
            Personalnummer = settings.Personalnummer,
            ExportPath = settings.ExportPath,
            Klasse = settings.Klasse,
            Jahresurlaub = settings.Jahresurlaub,
            Workdays = settings.Workdays,
            StudentActive = settings.StudentActive,
            Bundesland = settings.Bundesland,
            VocationalSchool = settings.VocationalSchool
        };
    }

    public async Task SaveAsync(AppSettings settings)
    {
        var path = _paths.GetSettingsFilePath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $"settings_{Guid.NewGuid():N}.tmp");
        
        // Log save attempt for diagnostic purposes
        System.Diagnostics.Debug.WriteLine($"[SettingsService.SaveAsync] Saving settings to: {path}");
        System.Diagnostics.Debug.WriteLine($"[SettingsService.SaveAsync] Vorname: '{settings.Vorname}', Nachname: '{settings.Nachname}'");
        
        await using (var stream = File.Create(tempPath))
        {
            var settingsToSave = new AppSettings
            {
                Version = AppSettings.SchemaVersion,
                Name = settings.Name,
                Vorname = settings.Vorname,
                Nachname = settings.Nachname,
                Adresse = settings.Adresse,
                Abteilung = settings.Abteilung,
                Personalnummer = settings.Personalnummer,
                ExportPath = settings.ExportPath,
                Klasse = settings.Klasse,
                Jahresurlaub = settings.Jahresurlaub,
                Workdays = settings.Workdays,
                StudentActive = settings.StudentActive,
                Bundesland = settings.Bundesland,
                VocationalSchool = settings.VocationalSchool
            };
            await JsonSerializer.SerializeAsync(stream, settingsToSave, _jsonOptions);
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        File.Move(tempPath, path);
        System.Diagnostics.Debug.WriteLine($"[SettingsService.SaveAsync] Settings saved successfully");
    }
}
