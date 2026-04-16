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
        // Personal information
        VornameTextBox.Text = settings.Vorname;
        // If Nachname is empty but Name has value, try to extract last name from Name
        if (string.IsNullOrEmpty(settings.Nachname) && !string.IsNullOrEmpty(settings.Name))
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsWindow.LoadSettings] Detected legacy Name-only format, attempting migration: {settings.Name}");
            var parts = settings.Name.Split(new[] { ' ' }, 2);
            NachnameTextBox.Text = parts.Length > 1 ? parts[1] : settings.Name;
            System.Diagnostics.Debug.WriteLine($"[SettingsWindow.LoadSettings] Migrated Nachname: {NachnameTextBox.Text}");
        }
        else
        {
            NachnameTextBox.Text = settings.Nachname;
        }
        AdresseTextBox.Text = settings.Adresse;
        AbteilungTextBox.Text = settings.Abteilung;
        PersonalnummerTextBox.Text = settings.Personalnummer;
        KlasseTextBox.Text = settings.Klasse;
        JahresurlaubTextBox.Text = settings.Jahresurlaub.ToString(CultureInfo.InvariantCulture);
        
        // Export settings
        ExportPathTextBox.Text = settings.ExportPath ?? string.Empty;

        // Workdays checkboxes
        MondayCheckBox.IsChecked = settings.Workdays.Contains(DayOfWeek.Monday);
        TuesdayCheckBox.IsChecked = settings.Workdays.Contains(DayOfWeek.Tuesday);
        WednesdayCheckBox.IsChecked = settings.Workdays.Contains(DayOfWeek.Wednesday);
        ThursdayCheckBox.IsChecked = settings.Workdays.Contains(DayOfWeek.Thursday);
        FridayCheckBox.IsChecked = settings.Workdays.Contains(DayOfWeek.Friday);
        SaturdayCheckBox.IsChecked = settings.Workdays.Contains(DayOfWeek.Saturday);
        SundayCheckBox.IsChecked = settings.Workdays.Contains(DayOfWeek.Sunday);

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
                VocationalSchool = vocationalSchool
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
}
