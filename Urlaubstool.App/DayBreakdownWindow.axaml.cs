using Avalonia.Controls;
using System.Collections.ObjectModel;
using Urlaubstool.App.ViewModels;

namespace Urlaubstool.App;

public partial class DayBreakdownWindow : Window
{
    public DayBreakdownWindow()
    {
        InitializeComponent();
    }

    public DayBreakdownWindow(ObservableCollection<DayBreakdownItem> breakdown, int requestedDays)
    {
        InitializeComponent();
        
        BreakdownDataGrid.ItemsSource = breakdown;
        
        // Calculate total days from actual breakdown data instead of relying on passed parameter
        var totalDays = breakdown.Sum(item => item.Days);
        SummaryTextBlock.Text = $"Insgesamt: {totalDays:F1} Tage";
        
        // Mark rows with blocking days (full vocational school) - these prevent vacation intake
        BreakdownDataGrid.LoadingRow += (sender, args) =>
        {
            if (args.Row.DataContext is DayBreakdownItem item)
            {
                // Only mark as error if it's a full vocational school day (blocks vacation)
                if (item.Badges.Contains("Berufsschule (Ganztag)"))
                {
                    // Dark red for blocked days
                    args.Row.Background = new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.Parse("#FF5C6E"));
                }
            }
        };
    }
}
