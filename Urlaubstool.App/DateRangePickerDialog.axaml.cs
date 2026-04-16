using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Urlaubstool.App.Controls;
using Urlaubstool.Domain;

namespace Urlaubstool.App;

public partial class DateRangePickerDialog : Window
{
    public DateOnly? SelectedStartDate { get; private set; }
    public DateOnly? SelectedEndDate { get; private set; }

    public DateRangePickerDialog()
    {
        InitializeComponent();
    }

    public DateRangePickerDialog(DateOnly? startDate, DateOnly? endDate) : this()
    {
        var picker = this.FindControl<DateRangePickerControl>("DateRangePicker")!;
        
        if (startDate.HasValue && endDate.HasValue)
        {
            picker.SelectedStartDate = startDate;
            picker.SelectedEndDate = endDate;
        }
    }

    public DateRangePickerDialog(DateOnly? startDate, DateOnly? endDate, IPublicHolidayProvider? publicHolidayProvider, ISchoolHolidayProvider? schoolHolidayProvider, string state = "DE-BY", bool studentActive = false, IReadOnlyDictionary<DayOfWeek, Urlaubstool.Domain.VocationalSchoolDayType>? vocationalSchoolDays = null, IReadOnlyCollection<DateOnly>? approvedVacationDates = null) : this()
    {
        var picker = this.FindControl<DateRangePickerControl>("DateRangePicker")!;
        
        if (startDate.HasValue && endDate.HasValue)
        {
            picker.SelectedStartDate = startDate;
            picker.SelectedEndDate = endDate;
        }

        picker.PublicHolidayProvider = publicHolidayProvider;
        picker.SchoolHolidayProvider = schoolHolidayProvider;
        picker.State = state;
        picker.StudentActive = studentActive;
        picker.VocationalSchoolDays = vocationalSchoolDays ?? new Dictionary<DayOfWeek, Urlaubstool.Domain.VocationalSchoolDayType>();
        picker.ApprovedVacationDates = approvedVacationDates ?? Array.Empty<DateOnly>();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        var picker = this.FindControl<DateRangePickerControl>("DateRangePicker")!;
        
        if (!picker.SelectedStartDate.HasValue || !picker.SelectedEndDate.HasValue)
        {
            _ = ShowMessageBox("Fehler", "Bitte wählen Sie sowohl ein Start- als auch ein Enddatum aus.");
            return;
        }

        // Use the same error messages as MainWindow/Domain calculation.
        var validationErrors = picker.GetValidationErrors();
        if (validationErrors.Count > 0)
        {
            _ = ShowMessageBox("Fehler", string.Join("\n", validationErrors));
            return;
        }

        SelectedStartDate = picker.SelectedStartDate;
        SelectedEndDate = picker.SelectedEndDate;
        
        Close((SelectedStartDate, SelectedEndDate));
    }

    private async System.Threading.Tasks.Task ShowMessageBox(string title, string message)
    {
        var messageBox = new Window
        {
            Title = title,
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 15
        };

        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 13
        });

        var button = new Button
        {
            Content = "OK",
            Width = 80,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        button.Click += (s, e) => messageBox.Close();
        panel.Children.Add(button);

        messageBox.Content = panel;
        await messageBox.ShowDialog(this);
    }
}
