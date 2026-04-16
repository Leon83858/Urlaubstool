using Avalonia;
using System;

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
