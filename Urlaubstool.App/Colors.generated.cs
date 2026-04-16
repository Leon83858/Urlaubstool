// Generated bridge to expose Colors.axaml resources at compile time
// This class ensures theme resources are loaded before the UI is rendered
#nullable enable
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;

namespace Urlaubstool.App
{
    /// <summary>
    /// Static helper class to ensure theme color resources are loaded into the application resources
    /// before any UI is rendered. This prevents missing resource errors and white/unstyled controls.
    /// </summary>
    public static class ThemeResources
    {
        /// <summary>
        /// Ensures the Colors.axaml resource dictionary is loaded into the application resources.
        /// Safe to call multiple times - uses flag to prevent duplicate loading.
        /// </summary>
        public static void EnsureLoaded()
        {
            var app = Application.Current;
            if (app == null)
                return;

            var resources = app.Resources;
            
            // Check if already loaded
            if (HasPalette(resources))
            {
                resources["ThemeColorsLoaded"] = true;
                return;
            }

            // Load Colors.axaml resource dictionary
            if (AvaloniaXamlLoader.Load(new Uri("avares://Urlaubstool.App/Colors.axaml")) is ResourceDictionary colors)
            {
                resources.MergedDictionaries.Add(colors);
                resources["ThemeColorsLoaded"] = true;
            }
        }

        /// <summary>
        /// Checks if the color palette resources are already loaded by testing for key resources.
        /// </summary>
        private static bool HasPalette(IResourceDictionary resources)
        {
            // Check flag first
            if (resources.TryGetValue("ThemeColorsLoaded", out var flag) && flag is bool loaded && loaded)
            {
                return true;
            }

            // Verify key resources exist
            return TryGetResource(resources, "ColorBgLight", out _) &&
                   TryGetResource(resources, "ColorBgDark", out _) &&
                   TryGetResource(resources, "BrushShadowLight", out _);
        }

        /// <summary>
        /// Helper to safely try to get a resource from the resource dictionary.
        /// </summary>
        private static bool TryGetResource(IResourceDictionary resources, string key, out object? value)
        {
            if (resources is IResourceProvider provider &&
                provider.TryGetResource(key, ThemeVariant.Default, out value))
            {
                return true;
            }

            value = null;
            return false;
        }
    }
}
