using Avalonia;
using System;
using System.IO;

namespace Urlaubstool.App;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            Console.WriteLine("[DEBUG] Urlaubstool starting...");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Fatal exception: {ex}");
            System.Diagnostics.Debug.WriteLine($"[ERROR] Stack trace: {ex.StackTrace}");
            StartupDiagnostics.WriteFatal("Program.Main", ex);
            Environment.ExitCode = 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        Console.WriteLine("[DEBUG] Building AppBuilder...");
        return AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}

internal static class StartupDiagnostics
{
    public static void WriteFatal(string context, Exception ex)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var logsDir = Path.Combine(appData, "Urlaubstool", "Logs");
            Directory.CreateDirectory(logsDir);

            var path = Path.Combine(logsDir, $"startup_fatal_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.WriteAllText(path,
                $"Timestamp: {DateTime.Now:O}{Environment.NewLine}" +
                $"Context: {context}{Environment.NewLine}" +
                $"OS: {Environment.OSVersion}{Environment.NewLine}" +
                $"Framework: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}{Environment.NewLine}" +
                $"Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}{Environment.NewLine}{Environment.NewLine}" +
                ex);
        }
        catch
        {
            // Never throw from diagnostics logging.
        }
    }
}
