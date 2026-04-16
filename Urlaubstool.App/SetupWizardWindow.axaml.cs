using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Urlaubstool.App.ViewModels;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.Paths;
using Urlaubstool.Infrastructure.Settings;

namespace Urlaubstool.App;

public partial class SetupWizardWindow : Window
{
    private readonly SetupWizardViewModel _viewModel;
    private readonly SettingsService _settingsService;

    public SetupWizardWindow()
    {
        InitializeComponent();
        
        _viewModel = new SetupWizardViewModel();
        DataContext = _viewModel;

        var pathService = new PathService();
        _settingsService = new SettingsService(pathService);

        SetupUI();
    }

    private void SetupUI()
    {
        // Setup Bundesland ComboBox
        BundeslandComboBox.ItemsSource = _viewModel.Bundeslaender;

        // Setup vocational school ComboBoxes with default selection (index 0 = None)
        MondayVocationalComboBox.SelectedIndex = 0;
        TuesdayVocationalComboBox.SelectedIndex = 0;
        WednesdayVocationalComboBox.SelectedIndex = 0;
        ThursdayVocationalComboBox.SelectedIndex = 0;
        FridayVocationalComboBox.SelectedIndex = 0;
        SaturdayVocationalComboBox.SelectedIndex = 0;
        SundayVocationalComboBox.SelectedIndex = 0;

        // Show/hide student parameters panel based on checkbox
        StudentActiveCheckBox.IsCheckedChanged += (s, e) =>
        {
            StudentParametersPanel.IsVisible = StudentActiveCheckBox.IsChecked == true;
        };
        StudentParametersPanel.IsVisible = false;

        // Update visibility of validation error
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(_viewModel.ValidationError))
            {
                ValidationErrorTextBlock.IsVisible = !string.IsNullOrEmpty(_viewModel.ValidationError);
            }
        };
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        // Read values from UI controls
        _viewModel.Vorname = VornameTextBox.Text ?? string.Empty;
        _viewModel.Nachname = NachnameTextBox.Text ?? string.Empty;
        _viewModel.Adresse = AdresseTextBox.Text ?? string.Empty;
        _viewModel.Abteilung = AbteilungTextBox.Text ?? string.Empty;
        _viewModel.Personalnummer = PersonalnummerTextBox.Text ?? string.Empty;
        _viewModel.Klasse = KlasseTextBox.Text ?? string.Empty;
        _viewModel.Jahresurlaub = JahresurlaubTextBox.Text ?? "30";

        _viewModel.Monday = MondayCheckBox.IsChecked == true;
        _viewModel.Tuesday = TuesdayCheckBox.IsChecked == true;
        _viewModel.Wednesday = WednesdayCheckBox.IsChecked == true;
        _viewModel.Thursday = ThursdayCheckBox.IsChecked == true;
        _viewModel.Friday = FridayCheckBox.IsChecked == true;
        _viewModel.Saturday = SaturdayCheckBox.IsChecked == true;
        _viewModel.Sunday = SundayCheckBox.IsChecked == true;

        _viewModel.StudentActive = StudentActiveCheckBox.IsChecked == true;
        _viewModel.SelectedBundesland = BundeslandComboBox.SelectedItem as string;

        // Update vocational school settings from ComboBoxes
        _viewModel.VocationalSchool[DayOfWeek.Monday] = GetVocationalSchoolType(MondayVocationalComboBox.SelectedIndex);
        _viewModel.VocationalSchool[DayOfWeek.Tuesday] = GetVocationalSchoolType(TuesdayVocationalComboBox.SelectedIndex);
        _viewModel.VocationalSchool[DayOfWeek.Wednesday] = GetVocationalSchoolType(WednesdayVocationalComboBox.SelectedIndex);
        _viewModel.VocationalSchool[DayOfWeek.Thursday] = GetVocationalSchoolType(ThursdayVocationalComboBox.SelectedIndex);
        _viewModel.VocationalSchool[DayOfWeek.Friday] = GetVocationalSchoolType(FridayVocationalComboBox.SelectedIndex);
        _viewModel.VocationalSchool[DayOfWeek.Saturday] = GetVocationalSchoolType(SaturdayVocationalComboBox.SelectedIndex);
        _viewModel.VocationalSchool[DayOfWeek.Sunday] = GetVocationalSchoolType(SundayVocationalComboBox.SelectedIndex);

        if (!_viewModel.Validate())
        {
            return;
        }

        try
        {
            var settings = _viewModel.CreateSettings();
            await _settingsService.SaveAsync(settings);

            // Close wizard and open main window
            var mainWindow = new MainWindow();
            mainWindow.Show();
            Close();
        }
        catch (Exception ex)
        {
            _viewModel.ValidationError = $"Fehler beim Speichern: {ex.Message}";
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Exit_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private VocationalSchoolDayType GetVocationalSchoolType(int selectedIndex)
    {
        return selectedIndex switch
        {
            0 => VocationalSchoolDayType.None,
            1 => VocationalSchoolDayType.Half,
            2 => VocationalSchoolDayType.Full,
            _ => VocationalSchoolDayType.None
        };
    }
}
