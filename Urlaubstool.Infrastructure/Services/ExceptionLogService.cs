using Urlaubstool.Infrastructure.Paths;

namespace Urlaubstool.Infrastructure.Services;

/// <summary>
/// Provides exception logging functionality to capture detailed error information
/// for debugging and diagnostics purposes.
/// Writes full exception details including stack traces and inner exceptions to
/// timestamped log files in the application's Logs directory.
/// </summary>
public sealed class ExceptionLogService
{
    private readonly PathService _pathService;

    public ExceptionLogService(PathService pathService)
    {
        _pathService = pathService ?? throw new ArgumentNullException(nameof(pathService));
    }

    /// <summary>
    /// Logs a complete exception (including stack trace and inner exceptions) to a file.
    /// Creates a versioned log file to prevent overwrites.
    /// </summary>
    /// <param name="context">Brief description of where/why the exception occurred (e.g., "PDF Export")</param>
    /// <param name="ex">The exception to log</param>
    /// <returns>Absolute path to the log file that was written</returns>
    public string Write(string context, Exception ex)
    {
        if (ex == null)
            throw new ArgumentNullException(nameof(ex));

        if (string.IsNullOrWhiteSpace(context))
            context = "Unknown";

        try
        {
            // Ensure the Logs directory exists
            var logsDir = GetLogsDirectory();
            Directory.CreateDirectory(logsDir);

            // Build a versioned log filename
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var baseFileName = $"error_{context.Replace(" ", "_")}_{timestamp}";
            var logFilePath = BuildVersionedPath(logsDir, baseFileName, ".log");

            // Write comprehensive exception information
            using (var writer = new StreamWriter(logFilePath, append: false))
            {
                writer.WriteLine("=== Exception Log ===");
                writer.WriteLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                writer.WriteLine($"Context: {context}");
                writer.WriteLine();

                // Full exception details including all inner exceptions and stack trace
                writer.WriteLine("Full Exception Details:");
                writer.WriteLine(ex.ToString());
                writer.WriteLine();

                // Additional diagnostic information
                writer.WriteLine("=== Exception Chain ===");
                var current = ex;
                int level = 0;
                while (current != null)
                {
                    writer.WriteLine($"[Level {level}] {current.GetType().FullName}: {current.Message}");
                    current = current.InnerException;
                    level++;
                }
            }

            return logFilePath;
        }
        catch
        {
            // If logging fails, return a descriptive error path (not necessarily written)
            // This prevents the logging service from causing cascading failures
            return Path.Combine(GetLogsDirectory(), $"error_log_failed_{DateTime.Now:yyyyMMddHHmmss}.log");
        }
    }

    /// <summary>
    /// Gets the application's Logs directory path.
    /// Uses the AppDataDirectory as the base and appends "Logs".
    /// </summary>
    private string GetLogsDirectory()
    {
        var appDataDir = _pathService.GetAppDataDirectory();
        return Path.Combine(appDataDir, "Logs");
    }

    /// <summary>
    /// Builds a versioned file path by appending a version number if the file already exists.
    /// Example: "error_pdf_2025-01-14_10-30-45_v1.log"
    /// </summary>
    private static string BuildVersionedPath(string directory, string baseName, string extension)
    {
        var filePath = Path.Combine(directory, baseName + extension);

        if (!File.Exists(filePath))
            return filePath;

        // File exists, append version numbers until we find a unique name
        for (int version = 1; version <= 9999; version++)
        {
            var versionedPath = Path.Combine(directory, $"{baseName}_v{version}{extension}");
            if (!File.Exists(versionedPath))
                return versionedPath;
        }

        // Fallback (should never happen in practice)
        return filePath;
    }
}
