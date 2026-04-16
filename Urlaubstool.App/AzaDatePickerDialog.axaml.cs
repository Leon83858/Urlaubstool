using Avalonia.Controls;
using Avalonia.Interactivity;
using Urlaubstool.App.Controls;
using Urlaubstool.Domain;

namespace Urlaubstool.App;

public partial class AzaDatePickerDialog : Window
{
    public DateOnly? SelectedDate { get; private set; }

    public AzaDatePickerDialog()
    {
        InitializeComponent();
    }

    public AzaDatePickerDialog(
        DateOnly? initialDate,
        IPublicHolidayProvider? publicHolidayProvider,
        ISchoolHolidayProvider? schoolHolidayProvider,
        string state,
        bool studentActive,
        IReadOnlyDictionary<DayOfWeek, VocationalSchoolDayType> vocationalSchoolDays,
        IReadOnlyCollection<DateOnly>? approvedVacationDates = null,
        Urlaubstool.Infrastructure.Settings.ColorSettings? colorSettings = null) : this()
    {
        var picker = this.FindControl<DateRangePickerControl>("AzaCalendarPicker")!;
        picker.PublicHolidayProvider = publicHolidayProvider;
        picker.SchoolHolidayProvider = schoolHolidayProvider;
        picker.State = state;
        picker.StudentActive = studentActive;
        picker.VocationalSchoolDays = vocationalSchoolDays;
        picker.ApprovedVacationDates = approvedVacationDates ?? Array.Empty<DateOnly>();
        picker.SingleDateSelectionMode = true;
        picker.ColorSettings = colorSettings ?? Urlaubstool.Infrastructure.Settings.ColorSettings.CreateDefault();

        if (initialDate.HasValue)
        {
            picker.SelectedStartDate = initialDate;
            picker.SelectedEndDate = initialDate;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private async void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        var picker = this.FindControl<DateRangePickerControl>("AzaCalendarPicker")!;

        if (!picker.SelectedStartDate.HasValue)
        {
            await ShowMessageBox("Hinweis", "Bitte wählen Sie einen AZA-Tag aus.");
            return;
        }

        SelectedDate = picker.SelectedStartDate;
        Close(SelectedDate);
    }

    private async Task ShowMessageBox(string title, string message)
    {
        var messageBox = new Window
        {
            Title = title,
            Width = 380,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12
        };

        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 13
        });

        var ok = new Button
        {
            Content = "OK",
            Width = 80,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        ok.Click += (_, _) => messageBox.Close();
        panel.Children.Add(ok);

        messageBox.Content = panel;
        await messageBox.ShowDialog(this);
    }
}
