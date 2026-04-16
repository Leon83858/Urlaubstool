using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.Logging;
using Urlaubstool.Infrastructure.History;
using Urlaubstool.Infrastructure.Ledger;
using Urlaubstool.Infrastructure.Logging;
using Urlaubstool.Infrastructure.Paths;
using Urlaubstool.Infrastructure.Settings;
using System;
using System.IO;
using System.Collections.Generic;

namespace Urlaubstool.App;

/// <summary>
/// Main application class that initializes the Avalonia framework and manages application lifecycle.
/// Responsible for loading theme resources and determining which window to show (Setup vs Main).
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Initializes the application by loading XAML resources including theme definitions.
    /// This is called first before any UI is created.
    /// </summary>
    public override void Initialize()
    {
        Console.WriteLine("[DEBUG] App.Initialize()");
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Called after initialization is complete. This is where we set up the main window
    /// and apply theme settings. The theme must be loaded before any windows are created
    /// to prevent white/unstyled controls.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        Console.WriteLine("[DEBUG] App.OnFrameworkInitializationCompleted()");
        
        try
        {
            Console.WriteLine("[DEBUG] About to ensure theme resources...");
            // Ensure theme resources are loaded before creating any UI
            ThemeResources.EnsureLoaded();
            Console.WriteLine("[DEBUG] Theme resources loaded");
            
            // Force dark theme mode
            if (Application.Current is not null)
            {
                Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
                Console.WriteLine("[DEBUG] Dark theme set");
            }

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                Console.WriteLine("[DEBUG] Creating window...");
                
                var pathService = new PathService();
                
                // Skip migrations - they can happen async in background or be disabled
                // to prevent GUI blocking
                Console.WriteLine("[DEBUG] Skipping synchronous migrations (too slow)");
                
                // Check if settings exist to decide which window to show
                var settingsPath = pathService.GetSettingsFilePath();
                
                if (!File.Exists(settingsPath))
                {
                    Console.WriteLine("[DEBUG] No settings found. Starting SetupWizard.");
                    desktop.MainWindow = new SetupWizardWindow();
                    // Ensure app doesn't close when Wizard closes (since it opens MainWindow)
                    desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                }
                else
                {
                    // Always show MainWindow first - it will check settings on Load
                    Console.WriteLine("[DEBUG] Creating MainWindow instance...");
                    var mainWindow = new MainWindow();
                    Console.WriteLine("[DEBUG] MainWindow instance created");
                    desktop.MainWindow = mainWindow;
                    Console.WriteLine("[DEBUG] MainWindow created and assigned");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Exception in OnFrameworkInitializationCompleted: {ex}");
            System.Diagnostics.Debug.WriteLine($"[ERROR] Stack trace: {ex.StackTrace}");
        }

        base.OnFrameworkInitializationCompleted();
    }
}
