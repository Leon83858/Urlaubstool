using System.Runtime.InteropServices;

namespace Urlaubstool.Infrastructure.Paths;

/// <summary>
/// Centralizes file system locations for settings, ledger, and exports.
/// 
/// History format: JSONL is the canonical format going forward.
/// - GetHistoryFilePath() returns the primary history.jsonl file
/// - GetBadHistoryFilePath() returns the bad events file for unparseable lines
/// 
/// Legacy support: CSV files are no longer written but are migrated to JSONL on first run.
/// </summary>
public class PathService
{
    protected virtual bool IsMacOs() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    protected virtual bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    protected virtual string GetDocumentsDirectory() => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    protected virtual string GetRoamingAppDataDirectory() => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public virtual string GetAppDataDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable("URLAUBSTOOL_APPDATA");
        if (!string.IsNullOrEmpty(overridePath))
        {
            return overridePath;
        }

        // Check for legacy location in Documents first (backward compatibility)
        var legacyPath = Path.Combine(
            GetDocumentsDirectory(),
            "Urlaubstool");
        if (Directory.Exists(legacyPath))
        {
            return legacyPath;
        }

        if (IsMacOs())
        {
            return Path.Combine(GetRoamingAppDataDirectory(), "Urlaubstool");
        }

        if (IsWindows())
        {
            return Path.Combine(GetRoamingAppDataDirectory(), "Urlaubstool");
        }

        return Path.Combine(GetRoamingAppDataDirectory(), "Urlaubstool");
    }

    public virtual string GetSettingsFilePath() => Path.Combine(GetAppDataDirectory(), "Settings", "settings.json");

    public virtual string GetLedgerFilePath() => Path.Combine(GetAppDataDirectory(), "ledger.csv");

    /// <summary>
    /// Gets the canonical history file path (JSONL format).
    /// This is the primary storage for all vacation request events.
    /// </summary>
    public virtual string GetHistoryFilePath()
    {
        return Path.Combine(GetAppDataDirectory(), "History", "history.jsonl");
    }

    /// <summary>
    /// Gets the bad history file path (JSONL format).
    /// Lines that fail to parse are logged here for inspection.
    /// </summary>
    public virtual string GetBadHistoryFilePath()
    {
        return Path.Combine(GetAppDataDirectory(), "History", "history.bad.jsonl");
    }

    public virtual string GetExportDirectory()
    {
        if (IsWindows())
        {
            return Path.Combine(GetDocumentsDirectory(), "Urlaubstool", "Exports");
        }

        return Path.Combine(GetAppDataDirectory(), "Exports");
    }
}
