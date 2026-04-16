using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.Paths;
using Urlaubstool.Infrastructure.Settings;

namespace Urlaubstool.App;

/// <summary>
/// Settings window for editing user configuration.
/// Allows modification of personal info, work schedule, and student parameters.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    private AppSettings? _currentSettings;

    public SettingsWindow()
    {
        InitializeComponent();
        
        var pathService = new PathService();
        _settingsService = new SettingsService(pathService);

        Loaded += SettingsWindow_Loaded;
        SetupUI();
    }

    private async void SettingsWindow_Loaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _currentSettings = await _settingsService.LoadAsync();
        
        if (_currentSettings != null)
        {
            LoadSettings(_currentSettings);
        }
    }

    private void SetupUI()
    {
        // Setup Bundesland ComboBox
        var bundeslaender = new List<string>
        {
            "BW", "BY", "BE", "BB", "HB", "HH", "HE", "MV",
            "NI", "NRW", "RP", "SL", "SN", "ST", "SH", "TH"
        };
        BundeslandComboBox.ItemsSource = bundeslaender;

        // Setup vocational school ComboBoxes for all days of week
        var vocOptions = new List<string> { "Kein Schultag", "Halbtag", "Ganztag" };
        MondayVocCombo.ItemsSource = vocOptions;
        TuesdayVocCombo.ItemsSource = vocOptions;
        WednesdayVocCombo.ItemsSource = vocOptions;
        ThursdayVocCombo.ItemsSource = vocOptions;
        FridayVocCombo.ItemsSource = vocOptions;
        SaturdayVocCombo.ItemsSource = vocOptions;
        SundayVocCombo.ItemsSource = vocOptions;

        // Set defaults to "Kein Schultag"
        MondayVocCombo.SelectedIndex = 0;
        TuesdayVocCombo.SelectedIndex = 0;
        WednesdayVocCombo.SelectedIndex = 0;
        ThursdayVocCombo.SelectedIndex = 0;
        FridayVocCombo.SelectedIndex = 0;
        SaturdayVocCombo.SelectedIndex = 0;
        SundayVocCombo.SelectedIndex = 0;

        // Show/hide student parameters panel based on checkbox
        StudentActiveCheckBox.IsCheckedChanged += (s, e) =>
        {
            StudentParametersPanel.IsVisible = StudentActiveCheckBox.IsChecked == true;
        };
        StudentParametersPanel.IsVisible = false;
    }

    /// <summary>
    /// Loads settings into the UI controls.
    /// Handles migration from old Name-only format to Vorname+Nachname.
    /// </summary>
    private void LoadSettings(AppSettings settings)
    {
        // Ensure all controls are initialized
        var vornameTextBox = this.FindControl<TextBox>("VornameTextBox");
        var nachnameTextBox = this.FindControl<TextBox>("NachnameTextBox");
        var adresseTextBox = this.FindControl<TextBox>("AdresseTextBox");
        var abteilungTextBox = this.FindControl<TextBox>("AbteilungTextBox");
        var personalnummerTextBox = this.FindControl<TextBox>("PersonalnummerTextBox");
        var klasseTextBox = this.FindControl<TextBox>("KlasseTextBox");
        var jahresurlaubTextBox = this.FindControl<TextBox>("JahresurlaubTextBox");
        var exportPathTextBox = this.FindControl<TextBox>("ExportPathTextBox");
        
        // Control references for checkboxes
        var mondayCheckBox = this.FindControl<CheckBox>("MondayCheckBox");
        var tuesdayCheckBox = this.FindControl<CheckBox>("TuesdayCheckBox");
        var wednesdayCheckBox = this.FindControl<CheckBox>("WednesdayCheckBox");
        var thursdayCheckBox = this.FindControl<CheckBox>("ThursdayCheckBox");
        var fridayCheckBox = this.FindControl<CheckBox>("FridayCheckBox");
        var saturdayCheckBox = this.FindControl<CheckBox>("SaturdayCheckBox");
        var sundayCheckBox = this.FindControl<CheckBox>("SundayCheckBox");
        
        // Color setting textboxes
        var colorSchultagGanztag = this.FindControl<TextBox>("ColorSchultagGanztag");
        var colorWochenende = this.FindControl<TextBox>("ColorWochenende");
        var colorSchultagHalbtag = this.FindControl<TextBox>("ColorSchultagHalbtag");
        var colorGenehmigterUrlaub = this.FindControl<TextBox>("ColorGenehmigterUrlaub");
        var colorNormaltag = this.FindControl<TextBox>("ColorNormaltag");

        // Return early if critical controls are missing
        if (vornameTextBox == null || nachnameTextBox == null) return;

        // Personal info
        vornameTextBox.Text = settings.Vorname;
        
        // If Nachname is empty but Name has value, try to extract last name from Name
        if (string.IsNullOrEmpty(settings.Nachname) && !string.IsNullOrEmpty(settings.Name))
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsWindow.LoadSettings] Detected legacy Name-only format, attempting migration: {settings.Name}");
            var parts = settings.Name.Split(new[] { ' ' }, 2);
            nachnameTextBox.Text = parts.Length > 1 ? parts[1] : settings.Name;
            System.Diagnostics.Debug.WriteLine($"[SettingsWindow.LoadSettings] Migrated Nachname: {nachnameTextBox.Text}");
        }
        else
        {
            nachnameTextBox.Text = settings.Nachname;
        }
        
        if (adresseTextBox != null) adresseTextBox.Text = settings.Adresse;
        if (abteilungTextBox != null) abteilungTextBox.Text = settings.Abteilung;
        if (personalnummerTextBox != null) personalnummerTextBox.Text = settings.Personalnummer;
        if (klasseTextBox != null) klasseTextBox.Text = settings.Klasse;
        if (jahresurlaubTextBox != null) jahresurlaubTextBox.Text = settings.Jahresurlaub.ToString(CultureInfo.InvariantCulture);
        
        // Export settings
        if (exportPathTextBox != null) exportPathTextBox.Text = settings.ExportPath ?? string.Empty;

        // Workdays checkboxes
        if (mondayCheckBox != null) mondayCheckBox.IsChecked = settings.Workdays.Contains(DayOfWeek.Monday);
        if (tuesdayCheckBox != null) tuesdayCheckBox.IsChecked = settings.Workdays.Contains(DayOfWeek.Tuesday);
        if (wednesdayCheckBox != null) wednesdayCheckBox.IsChecked = settings.Workdays.Contains(DayOfWeek.Wednesday);
        if (thursdayCheckBox != null) thursdayCheckBox.IsChecked = settings.Workdays.Contains(DayOfWeek.Thursday);
        if (fridayCheckBox != null) fridayCheckBox.IsChecked = settings.Workdays.Contains(DayOfWeek.Friday);
        if (saturdayCheckBox != null) saturdayCheckBox.IsChecked = settings.Workdays.Contains(DayOfWeek.Saturday);
        if (sundayCheckBox != null) sundayCheckBox.IsChecked = settings.Workdays.Contains(DayOfWeek.Sunday);

        // Student parameters
        StudentActiveCheckBox.IsChecked = settings.StudentActive;
        if (settings.StudentActive)
        {
            StudentParametersPanel.IsVisible = true;
        }

        if (!string.IsNullOrEmpty(settings.Bundesland))
        {
            // Migration for NW -> NRW
            var bl = settings.Bundesland == "NW" ? "NRW" : settings.Bundesland;
            BundeslandComboBox.SelectedItem = bl;
        }

        // Load vocational school settings for all days
        MondayVocCombo.SelectedIndex = (int)settings.VocationalSchool.GetValueOrDefault(DayOfWeek.Monday, VocationalSchoolDayType.None);
        TuesdayVocCombo.SelectedIndex = (int)settings.VocationalSchool.GetValueOrDefault(DayOfWeek.Tuesday, VocationalSchoolDayType.None);
        WednesdayVocCombo.SelectedIndex = (int)settings.VocationalSchool.GetValueOrDefault(DayOfWeek.Wednesday, VocationalSchoolDayType.None);
        ThursdayVocCombo.SelectedIndex = (int)settings.VocationalSchool.GetValueOrDefault(DayOfWeek.Thursday, VocationalSchoolDayType.None);
        FridayVocCombo.SelectedIndex = (int)settings.VocationalSchool.GetValueOrDefault(DayOfWeek.Friday, VocationalSchoolDayType.None);
        SaturdayVocCombo.SelectedIndex = (int)settings.VocationalSchool.GetValueOrDefault(DayOfWeek.Saturday, VocationalSchoolDayType.None);
        SundayVocCombo.SelectedIndex = (int)settings.VocationalSchool.GetValueOrDefault(DayOfWeek.Sunday, VocationalSchoolDayType.None);

        // Load color settings
        if (colorSchultagGanztag != null || colorWochenende != null || colorSchultagHalbtag != null || colorGenehmigterUrlaub != null || colorNormaltag != null)
        {
            var colors = settings.ColorSettings ?? Urlaubstool.Infrastructure.Settings.ColorSettings.CreateDefault();
            if (colorSchultagGanztag != null) colorSchultagGanztag.Text = colors.SchultagGanztag;
            if (colorWochenende != null) colorWochenende.Text = colors.Wochenende;
            if (colorSchultagHalbtag != null) colorSchultagHalbtag.Text = colors.SchultagHalbtag;
            if (colorGenehmigterUrlaub != null) colorGenehmigterUrlaub.Text = colors.GenehmigterUrlaub;
            if (colorNormaltag != null) colorNormaltag.Text = colors.Normaltag;
            
            // Update color previews
            UpdateAllColorPreviews();
        }
    }

    /// <summary>
    /// Validates and saves settings when Save button is clicked.
    /// Uses robust decimal parsing with German culture support.
    /// </summary>
    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        // Validate Vorname and Nachname
        if (string.IsNullOrWhiteSpace(VornameTextBox.Text))
        {
            ShowError("Bitte geben Sie einen Vornamen ein.");
            return;
        }

        if (string.IsNullOrWhiteSpace(NachnameTextBox.Text))
        {
            ShowError("Bitte geben Sie einen Nachnamen ein.");
            return;
        }

        // Robust decimal parsing - try German culture first, then invariant
        decimal urlaub = 0;
        if (!decimal.TryParse(JahresurlaubTextBox.Text, NumberStyles.Number, new CultureInfo("de-DE"), out urlaub) &&
            !decimal.TryParse(JahresurlaubTextBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out urlaub))
        {
            ShowError("Bitte geben Sie einen gültigen Jahresurlaub ein (z.B. 30 oder 28,5).");
            return;
        }

        if (urlaub < 0)
        {
            ShowError("Jahresurlaub muss eine positive Zahl sein.");
            return;
        }

        // Collect selected workdays
        var workdays = new HashSet<DayOfWeek>();
        if (MondayCheckBox.IsChecked == true) workdays.Add(DayOfWeek.Monday);
        if (TuesdayCheckBox.IsChecked == true) workdays.Add(DayOfWeek.Tuesday);
        if (WednesdayCheckBox.IsChecked == true) workdays.Add(DayOfWeek.Wednesday);
        if (ThursdayCheckBox.IsChecked == true) workdays.Add(DayOfWeek.Thursday);
        if (FridayCheckBox.IsChecked == true) workdays.Add(DayOfWeek.Friday);
        if (SaturdayCheckBox.IsChecked == true) workdays.Add(DayOfWeek.Saturday);
        if (SundayCheckBox.IsChecked == true) workdays.Add(DayOfWeek.Sunday);

        if (workdays.Count == 0)
        {
            ShowError("Bitte wählen Sie mindestens einen Arbeitstag aus.");
            return;
        }

        // Collect vocational school settings for all days
        var vocationalSchool = Enum.GetValues<DayOfWeek>().ToDictionary(
            d => d,
            d => VocationalSchoolDayType.None
        );
        vocationalSchool[DayOfWeek.Monday] = (VocationalSchoolDayType)MondayVocCombo.SelectedIndex;
        vocationalSchool[DayOfWeek.Tuesday] = (VocationalSchoolDayType)TuesdayVocCombo.SelectedIndex;
        vocationalSchool[DayOfWeek.Wednesday] = (VocationalSchoolDayType)WednesdayVocCombo.SelectedIndex;
        vocationalSchool[DayOfWeek.Thursday] = (VocationalSchoolDayType)ThursdayVocCombo.SelectedIndex;
        vocationalSchool[DayOfWeek.Friday] = (VocationalSchoolDayType)FridayVocCombo.SelectedIndex;
        vocationalSchool[DayOfWeek.Saturday] = (VocationalSchoolDayType)SaturdayVocCombo.SelectedIndex;
        vocationalSchool[DayOfWeek.Sunday] = (VocationalSchoolDayType)SundayVocCombo.SelectedIndex;

        try
        {
            // Validate HEX color codes
            if (!IsValidHexColor(ColorSchultagGanztag.Text))
            {
                ShowError("Ganztägiger Schultag: Ungültiger HEX-Farbcode (Format: #RRGGBB)");
                return;
            }
            if (!IsValidHexColor(ColorWochenende.Text))
            {
                ShowError("Wochenende/Feiertag: Ungültiger HEX-Farbcode (Format: #RRGGBB)");
                return;
            }
            if (!IsValidHexColor(ColorSchultagHalbtag.Text))
            {
                ShowError("Halbtägiger Schultag: Ungültiger HEX-Farbcode (Format: #RRGGBB)");
                return;
            }
            if (!IsValidHexColor(ColorGenehmigterUrlaub.Text))
            {
                ShowError("Genehmigter Urlaub: Ungültiger HEX-Farbcode (Format: #RRGGBB)");
                return;
            }
            if (!IsValidHexColor(ColorNormaltag.Text))
            {
                ShowError("Normaltag: Ungültiger HEX-Farbcode (Format: #RRGGBB)");
                return;
            }

            var colorSettings = new Urlaubstool.Infrastructure.Settings.ColorSettings
            {
                SchultagGanztag = ColorSchultagGanztag.Text.Trim(),
                Wochenende = ColorWochenende.Text.Trim(),
                SchultagHalbtag = ColorSchultagHalbtag.Text.Trim(),
                GenehmigterUrlaub = ColorGenehmigterUrlaub.Text.Trim(),
                Normaltag = ColorNormaltag.Text.Trim()
            };

            var settings = new AppSettings
            {
                Name = $"{VornameTextBox.Text} {NachnameTextBox.Text}".Trim(), // Computed for backward compatibility
                Vorname = VornameTextBox.Text.Trim(),
                Nachname = NachnameTextBox.Text.Trim(),
                Adresse = AdresseTextBox.Text ?? string.Empty,
                Abteilung = AbteilungTextBox.Text ?? string.Empty,
                Personalnummer = PersonalnummerTextBox.Text ?? string.Empty,
                ExportPath = string.IsNullOrWhiteSpace(ExportPathTextBox.Text) ? null : ExportPathTextBox.Text,
                Klasse = KlasseTextBox.Text ?? string.Empty,
                Jahresurlaub = urlaub,
                Workdays = workdays,
                StudentActive = StudentActiveCheckBox.IsChecked == true,
                Bundesland = BundeslandComboBox.SelectedItem as string,
                VocationalSchool = vocationalSchool,
                ColorSettings = colorSettings
            };

            await _settingsService.SaveAsync(settings);
            Close();
        }
        catch (Exception ex)
        {
            ShowError($"Fehler beim Speichern: {ex.Message}");
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private bool IsValidHexColor(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        
        text = text.Trim();
        
        // Must start with # and be exactly 7 chars long (e.g., #RRGGBB)
        if (!text.StartsWith("#") || text.Length != 7)
            return false;
        
        // Remaining 6 chars must be valid hex digits
        return System.Text.RegularExpressions.Regex.IsMatch(text, "^#[0-9A-Fa-f]{6}$");
    }

    private async void BrowseExportPath_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Export-Ordner auswählen",
            AllowMultiple = false
        };

        var storageProvider = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.StorageProvider;
        if (storageProvider == null) return;

        var result = await storageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Export-Ordner auswählen",
            AllowMultiple = false
        });

        if (result.Count > 0)
        {
            ExportPathTextBox.Text = result[0].Path.LocalPath;
        }
    }

    private void ShowError(string message)
    {
        ValidationErrorTextBlock.Text = message;
        ValidationErrorTextBlock.IsVisible = true;
    }

    // Color preview and reset handlers
    private void OnColorTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateAllColorPreviews();
    }

    private void UpdateAllColorPreviews()
    {
        UpdateColorPreview(ColorSchultagGanztag, "ColorSchultagGanztagPreview");
        UpdateColorPreview(ColorWochenende, "ColorWochenendePreview");
        UpdateColorPreview(ColorSchultagHalbtag, "ColorSchultagHalbtag_Preview");
        UpdateColorPreview(ColorGenehmigterUrlaub, "ColorGenehmigterUrlaubPreview");
        UpdateColorPreview(ColorNormaltag, "ColorNormaltagPreview");
    }

    private void UpdateColorPreview(TextBox colorTextBox, string previewBorderName)
    {
        var previewBorder = this.FindControl<Border>(previewBorderName);
        if (previewBorder == null || string.IsNullOrWhiteSpace(colorTextBox.Text))
            return;

        try
        {
            var color = Avalonia.Media.Color.Parse(colorTextBox.Text);
            previewBorder.Background = new Avalonia.Media.SolidColorBrush(color);
        }
        catch
        {
            // Invalid color code - show default light gray
            previewBorder.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#CCCCCC"));
        }
    }

    private void ResetColorSchultagGanztag_Click(object? sender, RoutedEventArgs e)
    {
        ColorSchultagGanztag.Text = "#FFCDD2";
        UpdateColorPreview(ColorSchultagGanztag, "ColorSchultagGanztagPreview");
    }

    private void ResetColorWochenende_Click(object? sender, RoutedEventArgs e)
    {
        ColorWochenende.Text = "#C8E6C9";
        UpdateColorPreview(ColorWochenende, "ColorWochenendePreview");
    }

    private void ResetColorSchultagHalbtag_Click(object? sender, RoutedEventArgs e)
    {
        ColorSchultagHalbtag.Text = "#FFE0B2";
        UpdateColorPreview(ColorSchultagHalbtag, "ColorSchultagHalbtag_Preview");
    }

    private void ResetColorGenehmigterUrlaub_Click(object? sender, RoutedEventArgs e)
    {
        ColorGenehmigterUrlaub.Text = "#E1BEE7";
        UpdateColorPreview(ColorGenehmigterUrlaub, "ColorGenehmigterUrlaubPreview");
    }

    private void ResetColorNormaltag_Click(object? sender, RoutedEventArgs e)
    {
        ColorNormaltag.Text = "#0E1A2D";
        UpdateColorPreview(ColorNormaltag, "ColorNormaltagPreview");
    }
}
