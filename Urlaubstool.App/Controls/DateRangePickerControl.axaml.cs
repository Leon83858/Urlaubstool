using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Urlaubstool.Domain;

namespace Urlaubstool.App.Controls;

/// <summary>
/// A calendar-based date range picker control that allows users to select a start and end date
/// by clicking on dates in a calendar view with month navigation.
/// </summary>
public partial class DateRangePickerControl : UserControl
{
    private DateTime _displayedMonth = DateTime.Now;
    private DateOnly? _selectedStartDate;
    private DateOnly? _selectedEndDate;
    private bool _awaitingEndDate;
    private IPublicHolidayProvider? _publicHolidayProvider;
    private ISchoolHolidayProvider? _schoolHolidayProvider;
    private string _state = "BY"; // Default to Bavaria (BY instead of DE-BY)
    private bool _studentActive = false;
    private IReadOnlyDictionary<DayOfWeek, Domain.VocationalSchoolDayType> _vocationalSchoolDays = new Dictionary<DayOfWeek, Domain.VocationalSchoolDayType>();
    private HashSet<DateOnly> _approvedVacationDates = new();
    private bool _singleDateSelectionMode;
    private Urlaubstool.Infrastructure.Settings.ColorSettings _colorSettings = Urlaubstool.Infrastructure.Settings.ColorSettings.CreateDefault();

    public event EventHandler<DateRangeSelectedEventArgs>? DateRangeSelected;

    public DateRangePickerControl()
    {
        InitializeComponent();
        RenderCalendar();
        DebugProviders(); // Debug: Test if providers work
    }

    private void DebugProviders()
    {
        // Debug: Test providers with current date
        var today = DateOnly.FromDateTime(DateTime.Today);
        var tomorrow = today.AddDays(1);
        var weekend = today.AddDays((int)DayOfWeek.Saturday - (int)today.DayOfWeek);

        Console.WriteLine($"[DEBUG] Testing providers for state: {_state}");
        Console.WriteLine($"[DEBUG] Today ({today}): Weekend={today.DayOfWeek == DayOfWeek.Saturday || today.DayOfWeek == DayOfWeek.Sunday}, PublicHoliday={_publicHolidayProvider?.IsPublicHoliday(today, _state)}, SchoolHoliday={_schoolHolidayProvider?.IsSchoolHoliday(today, _state)}");
        Console.WriteLine($"[DEBUG] Tomorrow ({tomorrow}): Weekend={tomorrow.DayOfWeek == DayOfWeek.Saturday || tomorrow.DayOfWeek == DayOfWeek.Sunday}, PublicHoliday={_publicHolidayProvider?.IsPublicHoliday(tomorrow, _state)}, SchoolHoliday={_schoolHolidayProvider?.IsSchoolHoliday(tomorrow, _state)}");
        Console.WriteLine($"[DEBUG] Weekend ({weekend}): Weekend={weekend.DayOfWeek == DayOfWeek.Saturday || weekend.DayOfWeek == DayOfWeek.Sunday}, PublicHoliday={_publicHolidayProvider?.IsPublicHoliday(weekend, _state)}, SchoolHoliday={_schoolHolidayProvider?.IsSchoolHoliday(weekend, _state)}");
    }

    public IPublicHolidayProvider? PublicHolidayProvider
    {
        get => _publicHolidayProvider;
        set
        {
            _publicHolidayProvider = value;
            RenderCalendar();
        }
    }

    public ISchoolHolidayProvider? SchoolHolidayProvider
    {
        get => _schoolHolidayProvider;
        set
        {
            _schoolHolidayProvider = value;
            RenderCalendar();
            DebugProviders(); // Debug again after setting provider
        }
    }

    public string State
    {
        get => _state;
        set
        {
            _state = value;
            RenderCalendar();
        }
    }

    public bool StudentActive
    {
        get => _studentActive;
        set
        {
            _studentActive = value;
            RenderCalendar();
        }
    }

    public IReadOnlyDictionary<DayOfWeek, Domain.VocationalSchoolDayType> VocationalSchoolDays
    {
        get => _vocationalSchoolDays;
        set
        {
            _vocationalSchoolDays = value ?? new Dictionary<DayOfWeek, Domain.VocationalSchoolDayType>();
            RenderCalendar();
        }
    }

    public IReadOnlyCollection<DateOnly> ApprovedVacationDates
    {
        get => _approvedVacationDates;
        set
        {
            _approvedVacationDates = value?.ToHashSet() ?? new HashSet<DateOnly>();
            RenderCalendar();
        }
    }

    public bool SingleDateSelectionMode
    {
        get => _singleDateSelectionMode;
        set
        {
            _singleDateSelectionMode = value;
            RenderCalendar();
            UpdateSelectedRangeLabel();
        }
    }

    public Urlaubstool.Infrastructure.Settings.ColorSettings ColorSettings
    {
        get => _colorSettings;
        set
        {
            _colorSettings = value ?? Urlaubstool.Infrastructure.Settings.ColorSettings.CreateDefault();
            RenderCalendar();
        }
    }

    public DateOnly? SelectedStartDate
    {
        get => _selectedStartDate;
        set
        {
            _selectedStartDate = value;
            RenderCalendar();
            UpdateSelectedRangeLabel();
        }
    }

    public DateOnly? SelectedEndDate
    {
        get => _selectedEndDate;
        set
        {
            _selectedEndDate = value;
            RenderCalendar();
            UpdateSelectedRangeLabel();
        }
    }

    private void PreviousMonth_Click(object? sender, RoutedEventArgs e)
    {
        _displayedMonth = _displayedMonth.AddMonths(-1);
        RenderCalendar();
        UpdateMonthLabel();
    }

    private void NextMonth_Click(object? sender, RoutedEventArgs e)
    {
        _displayedMonth = _displayedMonth.AddMonths(1);
        RenderCalendar();
        UpdateMonthLabel();
    }

    private void UpdateMonthLabel()
    {
        var monthLabel = this.FindControl<TextBlock>("MonthYearLabel");
        if (monthLabel != null)
        {
            monthLabel.Text = _displayedMonth.ToString("MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("de-DE"));
        }
    }

    private void RenderCalendar()
    {
        var grid = this.FindControl<Grid>("CalendarGrid");
        if (grid == null) return;

        // Clear existing day cells (keep header row)
        var itemsToRemove = grid.Children.Where(c => Grid.GetRow(c) > 0).ToList();
        foreach (var item in itemsToRemove)
        {
            grid.Children.Remove(item);
        }

        // Get first day of month
        var firstDay = new DateTime(_displayedMonth.Year, _displayedMonth.Month, 1);
        var firstDayOfWeek = firstDay.DayOfWeek;

        // Adjust for Monday-based week (0 = Monday in ISO, but DayOfWeek has Sunday = 0)
        var startOffset = firstDayOfWeek == DayOfWeek.Sunday ? 6 : (int)firstDayOfWeek - 1;

        // Days in month
        var daysInMonth = DateTime.DaysInMonth(_displayedMonth.Year, _displayedMonth.Month);

        // Render days
        for (int i = 1; i <= daysInMonth; i++)
        {
            var date = new DateTime(_displayedMonth.Year, _displayedMonth.Month, i);
            var dateOnly = DateOnly.FromDateTime(date);
            
            var button = new Button
            {
                Content = i.ToString(),
                Padding = new Avalonia.Thickness(0),
                Margin = new Avalonia.Thickness(1),
                MinHeight = 40,
                MinWidth = 40,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                Tag = dateOnly
            };

            // Determine styling based on selection state
            UpdateDayButtonStyle(button, dateOnly);

            button.Click += (s, e) => DayButton_Click(dateOnly);

            // Position in grid
            var col = (startOffset + i - 1) % 7;
            var row = (startOffset + i - 1) / 7 + 1;

            Grid.SetColumn(button, col);
            Grid.SetRow(button, row);
            grid.Children.Add(button);
        }

        UpdateMonthLabel();
        UpdateLegendColors();
    }

    private void UpdateLegendColors()
    {
        var legendSchultagGanztag = this.FindControl<Border>("LegendSchultagGanztag");
        var legendWochenende = this.FindControl<Border>("LegendWochenende");
        var legendHalbtag = this.FindControl<Border>("LegendHalbtag");
        var legendGenehmigterUrlaub = this.FindControl<Border>("LegendGenehmigterUrlaub");
        var legendNormaltag = this.FindControl<Border>("LegendNormaltag");

        if (legendSchultagGanztag != null)
            legendSchultagGanztag.Background = new SolidColorBrush(Color.Parse(_colorSettings.SchultagGanztag));

        if (legendWochenende != null)
            legendWochenende.Background = new SolidColorBrush(Color.Parse(_colorSettings.Wochenende));

        if (legendHalbtag != null)
            legendHalbtag.Background = new SolidColorBrush(Color.Parse(_colorSettings.SchultagHalbtag));

        if (legendGenehmigterUrlaub != null)
            legendGenehmigterUrlaub.Background = new SolidColorBrush(Color.Parse(_colorSettings.GenehmigterUrlaub));

        if (legendNormaltag != null)
            legendNormaltag.Background = new SolidColorBrush(Color.Parse(_colorSettings.Normaltag));
    }

    private void UpdateDayButtonStyle(Button button, DateOnly dateOnly)
    {
        var isStartDate = dateOnly == _selectedStartDate;
        var isEndDate = dateOnly == _selectedEndDate;
        var isInRange = _selectedStartDate.HasValue && _selectedEndDate.HasValue &&
                       dateOnly > _selectedStartDate.Value && dateOnly < _selectedEndDate.Value;
        var isToday = dateOnly == DateOnly.FromDateTime(DateTime.Today);
        var dayType = GetDayType(dateOnly);

        // Debug: Log the day type for a few specific dates
        if (dateOnly.Day <= 7 && _displayedMonth.Month == DateTime.Today.Month)
        {
            Console.WriteLine($"[DEBUG] Day {dateOnly}: Type={dayType}, Start={isStartDate}, End={isEndDate}, InRange={isInRange}, Today={isToday}");
        }

        button.Padding = new Avalonia.Thickness(0);
        button.Margin = new Avalonia.Thickness(1);
        button.MinHeight = 40;
        button.MinWidth = 40;
        button.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        button.VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;

        var defaultBackground = (IBrush?)Avalonia.Application.Current?.Resources["Brush.Control"] ?? new SolidColorBrush(Color.Parse("#0E1A2D"));
        var defaultForeground = (IBrush?)Avalonia.Application.Current?.Resources["Brush.Text"] ?? new SolidColorBrush(Color.Parse("#E7EEF8"));
        var defaultBorder = (IBrush?)Avalonia.Application.Current?.Resources["Brush.Border"] ?? new SolidColorBrush(Color.Parse("#20324F"));

        // Determine day type color scheme (border and text color for special days)
        IBrush? specialBorderBrush = null;
        IBrush? specialForeground = null;

        switch (dayType)
        {
            case DayType.ApprovedVacation:
                specialBorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8E24AA"));
                specialForeground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#4A148C"));
                break;
            case DayType.SchoolHoliday:
                specialBorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E57373"));
                specialForeground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#B71C1C"));
                break;
            case DayType.PublicHolidayOrWeekend:
                specialBorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#66BB6A"));
                specialForeground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1B5E20"));
                break;
            case DayType.HalfDayOnly:
                specialBorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF9800"));
                specialForeground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E65100"));
                break;
        }

        if (isStartDate || isEndDate)
        {
            // Start/End dates: Accent color background with special day border preserved
            button.Background = (IBrush?)Avalonia.Application.Current?.Resources["Brush.Accent"] ?? new SolidColorBrush(Color.Parse("#2196F3"));
            button.Foreground = specialForeground ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFFFFF"));
            button.FontWeight = Avalonia.Media.FontWeight.Bold;
            button.BorderBrush = specialBorderBrush ?? new SolidColorBrush(Color.Parse("#0000FF"));
            button.BorderThickness = new Avalonia.Thickness(2);
        }
        else if (isInRange)
        {
            // In range: Light highlight with special day border preserved
            var accentBrush = (SolidColorBrush?)Avalonia.Application.Current?.Resources["Brush.Accent"];
            if (accentBrush != null)
            {
                var accentColor = accentBrush.Color;
                button.Background = new SolidColorBrush(Color.FromArgb((byte)(accentColor.A * 0.3), accentColor.R, accentColor.G, accentColor.B));
                button.Foreground = specialForeground ?? defaultForeground;
            }
            else
            {
                button.Background = new SolidColorBrush(Color.Parse("#BBDEFB"));
                button.Foreground = specialForeground ?? new SolidColorBrush(Color.Parse("#1565C0"));
            }
            button.FontWeight = Avalonia.Media.FontWeight.Normal;
            button.BorderBrush = specialBorderBrush ?? defaultBorder;
            button.BorderThickness = new Avalonia.Thickness(1);
        }
        else
        {
            // Regular days with day type coloring
            if (dayType == DayType.SchoolHoliday)
            {
                Console.WriteLine($"[DEBUG] Applying SchoolHoliday style for {dateOnly}");
                button.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(_colorSettings.SchultagGanztag));
                button.Foreground = specialForeground;
                button.BorderBrush = specialBorderBrush;
                button.BorderThickness = new Avalonia.Thickness(1);
            }
            else if (dayType == DayType.PublicHolidayOrWeekend)
            {
                Console.WriteLine($"[DEBUG] Applying PublicHolidayOrWeekend style for {dateOnly}");
                button.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(_colorSettings.Wochenende));
                button.Foreground = specialForeground;
                button.BorderBrush = specialBorderBrush;
                button.BorderThickness = new Avalonia.Thickness(1);
            }
            else if (dayType == DayType.HalfDayOnly)
            {
                button.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(_colorSettings.SchultagHalbtag));
                button.Foreground = specialForeground;
                button.BorderBrush = specialBorderBrush;
                button.BorderThickness = new Avalonia.Thickness(1);
            }
            else if (dayType == DayType.ApprovedVacation)
            {
                // Gaming-style loot purple for already approved vacation days.
                button.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(_colorSettings.GenehmigterUrlaub));
                button.Foreground = specialForeground;
                button.BorderBrush = specialBorderBrush;
                button.BorderThickness = new Avalonia.Thickness(1);
            }
            else
            {
                // Normal day
                button.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(_colorSettings.Normaltag));
                button.Foreground = defaultForeground;
                button.BorderBrush = defaultBorder;
                button.BorderThickness = new Avalonia.Thickness(1);
            }

            // Override for today
            if (isToday)
            {
                button.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F9A825"));
                button.BorderThickness = new Avalonia.Thickness(2);
            }
        }
    }

    private enum DayType
    {
        Normal,
        ApprovedVacation,
        SchoolHoliday,
        PublicHolidayOrWeekend,
        HalfDayOnly
    }

    private DayType GetDayType(DateOnly date)
    {
        var dayOfWeek = date.DayOfWeek;

        // Check if it's a weekend or public holiday (always green)
        if (dayOfWeek == DayOfWeek.Saturday || dayOfWeek == DayOfWeek.Sunday)
        {
            return DayType.PublicHolidayOrWeekend;
        }

        if (_publicHolidayProvider?.IsPublicHoliday(date, _state) == true)
        {
            return DayType.PublicHolidayOrWeekend;
        }

        // School holiday days are normal workdays for this view (dunkelblau)
        // and must not be marked as vocational school days.
        if (_schoolHolidayProvider?.IsSchoolHoliday(date, _state) == true)
        {
            return DayType.Normal;
        }

        // Mark already approved vacation days in purple.
        if (_approvedVacationDates.Contains(date))
        {
            return DayType.ApprovedVacation;
        }

        // If student mode is active, check vocational school days first
        if (_studentActive && _vocationalSchoolDays.TryGetValue(dayOfWeek, out var vocationalType))
        {
            if (vocationalType == Domain.VocationalSchoolDayType.Full)
            {
                return DayType.SchoolHoliday; // Full vocational school day = red
            }
            else if (vocationalType == Domain.VocationalSchoolDayType.Half)
            {
                return DayType.HalfDayOnly; // Half vocational school day = orange
            }
            // If None, fall through to normal
        }

        // Otherwise normal day
        return DayType.Normal;
    }

    private void DayButton_Click(DateOnly dateOnly)
    {
        if (_singleDateSelectionMode)
        {
            _selectedStartDate = dateOnly;
            _selectedEndDate = dateOnly;
            _awaitingEndDate = false;
            DateRangeSelected?.Invoke(this, new DateRangeSelectedEventArgs(_selectedStartDate.Value, _selectedEndDate.Value));
            RenderCalendar();
            UpdateSelectedRangeLabel();
            return;
        }

        if (!_selectedStartDate.HasValue)
        {
            // First selection: set start date
            _selectedStartDate = dateOnly;
            _selectedEndDate = null;
            _awaitingEndDate = true;
        }
        else if (!_selectedEndDate.HasValue)
        {
            // Second selection: set end date
            if (dateOnly > _selectedStartDate.Value)
            {
                _selectedEndDate = dateOnly;
                _awaitingEndDate = false;
                DateRangeSelected?.Invoke(this, new DateRangeSelectedEventArgs(_selectedStartDate.Value, _selectedEndDate.Value));
            }
            else if (dateOnly < _selectedStartDate.Value)
            {
                // Swap if user selected earlier date
                _selectedEndDate = _selectedStartDate;
                _selectedStartDate = dateOnly;
                _awaitingEndDate = false;
                DateRangeSelected?.Invoke(this, new DateRangeSelectedEventArgs(_selectedStartDate.Value, _selectedEndDate.Value));
            }
            else
            {
                // Same date: select a one-day vacation
                _selectedEndDate = _selectedStartDate;
                _awaitingEndDate = false;
                DateRangeSelected?.Invoke(this, new DateRangeSelectedEventArgs(_selectedStartDate.Value, _selectedEndDate.Value));
            }
        }
        else
        {
            // Already have both dates - reset and start over
            _selectedStartDate = dateOnly;
            _selectedEndDate = null;
            _awaitingEndDate = true;
        }

        RenderCalendar();
        UpdateSelectedRangeLabel();
    }

    private void UpdateSelectedRangeLabel()
    {
        var rangeLabel = this.FindControl<TextBlock>("SelectedRangeLabel");
        var daysLabel = this.FindControl<TextBlock>("DaysCountLabel");
        var errorLabel = this.FindControl<TextBlock>("ErrorLabel");

        if (rangeLabel == null || daysLabel == null) return;

        if (_selectedStartDate.HasValue && _selectedEndDate.HasValue)
        {
            rangeLabel.Text = $"Zeitraum: {_selectedStartDate:dd.MM.yyyy} - {_selectedEndDate:dd.MM.yyyy}";
            
            // Use intelligent day counting that considers half-days as 0.5 days
            var totalDays = CountVacationDays();
            
            // Check if selection includes full school days
            var errorMessages = GetValidationErrors();
            
            if (errorMessages.Count > 0)
            {
                if (errorLabel != null)
                {
                    errorLabel.IsVisible = true;
                    errorLabel.Text = "⚠️ " + string.Join("\n", errorMessages);
                }
            }
            else
            {
                if (errorLabel != null) errorLabel.IsVisible = false;
            }

            // Format day count - show decimal if it's a half-day fraction
            if (totalDays % 1 == 0)
            {
                daysLabel.Text = $"{(int)totalDays} Tage";
            }
            else
            {
                daysLabel.Text = $"{totalDays} Tage";
            }
        }
        else if (_selectedStartDate.HasValue)
        {
            rangeLabel.Text = $"Startdatum: {_selectedStartDate:dd.MM.yyyy}";
            daysLabel.Text = _awaitingEndDate ? "Bitte Enddatum wählen..." : "";
            if (errorLabel != null) errorLabel.IsVisible = false;
        }
        else
        {
            rangeLabel.Text = "Zeitraum: Nicht gewählt";
            daysLabel.Text = "";
            if (errorLabel != null) errorLabel.IsVisible = false;
        }
    }

    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();

        if (!_selectedStartDate.HasValue || !_selectedEndDate.HasValue)
            return errors;

        var fullSchoolDayDates = GetFullSchoolDayDatesInSelection();
        if (fullSchoolDayDates.Count > 0)
        {
            // Keep wording aligned with MainWindow/Domain validation.
            errors.Add("An ganztägigen Schultagen kann kein Urlaub genommen werden.");
            errors.Add($"Betroffene Termine: {string.Join(", ", fullSchoolDayDates)}");
        }

        return errors;
    }

    private List<string> GetFullSchoolDayDatesInSelection()
    {
        var dates = new List<string>();

        if (!_selectedStartDate.HasValue || !_selectedEndDate.HasValue)
            return dates;

        var current = _selectedStartDate.Value;
        while (current <= _selectedEndDate.Value)
        {
            if (GetDayType(current) == DayType.SchoolHoliday)
            {
                dates.Add(current.ToString("dd.MM.yyyy"));
            }

            current = current.AddDays(1);
        }

        return dates;
    }

    private bool ContainsHalfDays()
    {
        if (!_selectedStartDate.HasValue || !_selectedEndDate.HasValue)
            return false;

        var current = _selectedStartDate.Value;
        while (current <= _selectedEndDate.Value)
        {
            // School holiday days are full normal vacation days (not half-days).
            if (_schoolHolidayProvider?.IsSchoolHoliday(current, _state) == true)
            {
                current = current.AddDays(1);
                continue;
            }

            if (_studentActive && _vocationalSchoolDays.TryGetValue(current.DayOfWeek, out var dayType))
            {
                if (dayType == Domain.VocationalSchoolDayType.Half)
                    return true;
            }
            current = current.AddDays(1);
        }
        return false;
    }

    public void Reset()
    {
        _selectedStartDate = null;
        _selectedEndDate = null;
        _awaitingEndDate = false;
        RenderCalendar();
        UpdateSelectedRangeLabel();
    }

    /// <summary>
    /// Returns true if any full vocational school day (red day) exists in the selected range.
    /// </summary>
    public bool IsEntireVacationSchoolDays()
    {
        return GetFullSchoolDayDatesInSelection().Count > 0;
    }

    /// <summary>
    /// Counts vacation days considering half-days as 0.5 days.
    /// Returns total vacation days as a decimal (e.g., 1.5 for one full day and one half day).
    /// </summary>
    private decimal CountVacationDays()
    {
        if (!_selectedStartDate.HasValue || !_selectedEndDate.HasValue)
            return 0;

        decimal totalDays = 0;
        var current = _selectedStartDate.Value;

        while (current <= _selectedEndDate.Value)
        {
            // Use the same classification as the calendar color coding.
            // Green days (weekend/public holiday) do not consume vacation days.
            var dayType = GetDayType(current);
            switch (dayType)
            {
                case DayType.PublicHolidayOrWeekend:
                    break;
                case DayType.HalfDayOnly:
                    totalDays += 0.5m;
                    break;
                default:
                    totalDays += 1;
                    break;
            }

            current = current.AddDays(1);
        }

        return totalDays;
    }
}

public class DateRangeSelectedEventArgs : EventArgs
{
    public DateOnly StartDate { get; }
    public DateOnly EndDate { get; }

    public DateRangeSelectedEventArgs(DateOnly startDate, DateOnly endDate)
    {
        StartDate = startDate;
        EndDate = endDate;
    }
}
