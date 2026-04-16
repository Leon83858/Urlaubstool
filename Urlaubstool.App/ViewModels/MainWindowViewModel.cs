using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media;
using Microsoft.Extensions.Logging;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.History;
using Urlaubstool.Infrastructure.Holidays;
using Urlaubstool.Infrastructure.Ledger;
using Urlaubstool.Infrastructure.Logging;
using Urlaubstool.Infrastructure.Paths;
using Urlaubstool.Infrastructure.Pdf;
using Urlaubstool.Infrastructure.Services;
using Urlaubstool.Infrastructure.Settings;

namespace Urlaubstool.App.ViewModels;

/// <summary>
/// ViewModel for the main vacation request window
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    private readonly VacationCalculator _calculator;
    private readonly SchoolHolidayProvider _schoolHolidayProvider; // keep reference to reload
    private readonly SettingsService _settingsService;
    private readonly IHistoryService _historyService;
    private readonly PdfExportService _pdfService;
    private readonly PdfTemplateFormFillExportService _pdfFormFillService;
    private readonly PathService _pathService;
    private readonly ILogger<MainWindowViewModel> _logger;
    private AppSettings? _settings;
    private CalculationResult? _lastResult;
    private Guid? _currentRequestId; // Track current request being edited

    private DateOnly _startDate;
    private DateOnly _endDate;
    private bool _startHalfDay;
    private bool _endHalfDay;
    // Year is derived from the StartDate to avoid inconsistent user-editable year fields
    private string _errorMessage = string.Empty;
    private bool _hasErrors;
    private bool _isBusy;
    private string _startDateError = string.Empty;
    private string _endDateError = string.Empty;
    private decimal _requestedDays;      // Tage des aktuellen Antrags ("Beantragt")
    private decimal _approvedDays;       // Bereits genehmigte Tage ("Genehmigt")
    private decimal _pendingHistoryDays; // Aus der Historie: "Exportiert" aber noch nicht genehmigt
    private decimal _remainingDays;      // Resturlaub
    private IBrush _remainingDaysColor = new SolidColorBrush(Color.Parse("#E5EEF9")); // Farbe für Resturlaub

    private int _historyFilterYear;
    private int _historyFilterStatusIndex;
    private int _historySortIndex;
    private bool _hasAzaDays;

    public int HistoryFilterYear
    {
        get => _historyFilterYear;
        set
        {
            if (SetProperty(ref _historyFilterYear, value))
            {
                _ = LoadHistoryEntriesAsync();
            }
        }
    }

    public int HistorySortIndex
    {
        get => _historySortIndex;
        set
        {
            if (SetProperty(ref _historySortIndex, value))
            {
                _ = LoadHistoryEntriesAsync();
            }
        }
    }

    public int HistoryFilterStatusIndex
    {
        get => _historyFilterStatusIndex;
        set
        {
            if (SetProperty(ref _historyFilterStatusIndex, value))
            {
                _ = LoadHistoryEntriesAsync();
            }
        }
    }

    public ICommand RefreshHistoryCommand { get; }

    public MainWindowViewModel()
    {
        var pathService = new PathService();
        var publicHolidayProvider = new PublicHolidayProvider();
        _schoolHolidayProvider = new SchoolHolidayProvider();
        
        // Set public provider reference
        PublicHolidayProvider = publicHolidayProvider;
        
        // Initial load of cached data if it exists
        var cachePath = Path.Combine(pathService.GetAppDataDirectory(), "school_holidays_cache.json");
        _schoolHolidayProvider.Reload(cachePath);
        
        _calculator = new VacationCalculator(publicHolidayProvider, _schoolHolidayProvider);
        _settingsService = new SettingsService(pathService);
        _pdfService = new PdfExportService(pathService);
        _pdfFormFillService = new PdfTemplateFormFillExportService(pathService.GetExportDirectory());
        _pathService = pathService;
        _logger = new ConsoleLogger<MainWindowViewModel>();

        // Setup history service
        var storeLogger = new ConsoleLogger<JsonlHistoryStore>();
        var serviceLogger = new ConsoleLogger<HistoryService>();
        var historyStore = new JsonlHistoryStore(pathService, storeLogger);
        _historyService = new HistoryService(historyStore, serviceLogger);
        _pathService = pathService;

        // StartDate already initialized; Year is derived from StartDate.Year
        _startDate = DateOnly.FromDateTime(DateTime.Today);
        _endDate = DateOnly.FromDateTime(DateTime.Today.AddDays(4));
        
        Breakdown = new ObservableCollection<DayBreakdownItem>();
        AzaDays = new ObservableCollection<AzaDayItem>();
        HistoryEntries = new ObservableCollection<MainHistoryEntryViewModel>();
        
        RefreshHistoryCommand = new AsyncRelayCommand(async () => await LoadHistoryEntriesAsync());
        
        _historyFilterYear = DateTime.Today.Year;
        _historyFilterStatusIndex = 0; // "Alle"
        _historySortIndex = 0; // Date Ascending
    }

    public DateOnly StartDate
    {
        get => _startDate;
        set
        {
            if (SetProperty(ref _startDate, value))
            {
                _currentRequestId = null;
                Calculate();
            }
        }
    }

    public DateOnly EndDate
    {
        get => _endDate;
        set
        {
            if (SetProperty(ref _endDate, value))
            {
                _currentRequestId = null;
                Calculate();
            }
        }
    }

    public bool StartHalfDay
    {
        get => _startHalfDay;
        set
        {
            if (SetProperty(ref _startHalfDay, value))
            {
                _currentRequestId = null;
                Calculate();
            }
        }
    }

    public bool EndHalfDay
    {
        get => _endHalfDay;
        set
        {
            if (SetProperty(ref _endHalfDay, value))
            {
                _currentRequestId = null;
                Calculate();
            }
        }
    }

    /// <summary>
    /// The vacation year for the current request. Derived from <see cref="StartDate"/> to avoid UI inconsistency.
    /// </summary>
    public int Year => StartDate.Year;

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>
    /// Field-level error for StartDate (displayed near control)
    /// </summary>
    public string StartDateError
    {
        get => _startDateError;
        set => SetProperty(ref _startDateError, value);
    }

    /// <summary>
    /// Field-level error for EndDate (displayed near control)
    /// </summary>
    public string EndDateError
    {
        get => _endDateError;
        set => SetProperty(ref _endDateError, value);
    }

    public bool HasErrors
    {
        get => _hasErrors;
        set => SetProperty(ref _hasErrors, value);
    }

    /// <summary>
    /// Indicates a long-running operation (e.g., export) is in progress.
    /// UI can bind to this to disable controls and show progress.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public decimal TotalDays => RequestedDays;  // Alias für Backward-Compatibility

    public decimal RequestedDays
    {
        get => _requestedDays;
        set => SetProperty(ref _requestedDays, value);
    }

    public IBrush RemainingDaysColor
    {
        get => _remainingDaysColor;
        set => SetProperty(ref _remainingDaysColor, value);
    }

    public decimal ApprovedDays
    {
        get => _approvedDays;
        set => SetProperty(ref _approvedDays, value);
    }

    public decimal PendingHistoryDays
    {
        get => _pendingHistoryDays;
        set => SetProperty(ref _pendingHistoryDays, value);
    }

    public decimal RemainingDays
    {
        get => _remainingDays;
        set => SetProperty(ref _remainingDays, value);
    }

    /// <summary>
    /// Gets the annual vacation entitlement from settings.
    /// This value is fixed and does not change based on approved or requested days.
    /// Used for displaying the "Jahresurlaub" (annual entitlement) figure in the UI.
    /// </summary>
    public decimal AnnualEntitlement => _settings?.Jahresurlaub ?? 0m;

    public ObservableCollection<DayBreakdownItem> Breakdown { get; }

    public ObservableCollection<AzaDayItem> AzaDays { get; }

    public bool HasAzaDays
    {
        get => _hasAzaDays;
        set
        {
            if (SetProperty(ref _hasAzaDays, value))
            {
                _currentRequestId = null;
                Calculate();
            }
        }
    }

    public ObservableCollection<MainHistoryEntryViewModel> HistoryEntries { get; }

    // Public access to providers for calendar coloring
    public IPublicHolidayProvider? PublicHolidayProvider { get; private set; }
    public ISchoolHolidayProvider? SchoolHolidayProvider => _schoolHolidayProvider;
    public string State => "BY"; // Bavaria state code for holiday providers
    
    public bool StudentActive => _settings?.StudentActive ?? false;
    public IReadOnlyDictionary<DayOfWeek, Domain.VocationalSchoolDayType> VocationalSchoolSettings => 
        _settings?.VocationalSchool ?? new Dictionary<DayOfWeek, Domain.VocationalSchoolDayType>();

    public void AddAzaDay()
    {
        var item = new AzaDayItem(RemoveAzaDay);
        item.PropertyChanged += OnAzaDayPropertyChanged;
        AzaDays.Add(item);
        Calculate();
    }

    public void RemoveAzaDay(AzaDayItem item)
    {
        item.PropertyChanged -= OnAzaDayPropertyChanged;
        AzaDays.Remove(item);
        Calculate();
    }

    private void OnAzaDayPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AzaDayItem.Date))
        {
            Calculate();
        }
    }

    private HashSet<DateOnly> GetAzaDatesFromUI()
    {
        // If the user has disabled AZA-days, ignore any values present in the UI
        if (!HasAzaDays)
        {
            return new HashSet<DateOnly>();
        }

        var azaDates = new HashSet<DateOnly>();
        foreach (var item in AzaDays)
        {
            if (item.Date.HasValue)
            {
                // Use LocalDateTime to avoid unintended date shifts caused by offsets
                var convertedDate = DateOnly.FromDateTime(item.Date.Value.LocalDateTime);
                System.Diagnostics.Debug.WriteLine($"[GetAzaDatesFromUI] AZA Item: {item.Date.Value} (Offset: {item.Date.Value.Offset}) → LocalDateTime: {item.Date.Value.LocalDateTime} → DateOnly: {convertedDate}");
                azaDates.Add(convertedDate);
            }
        }
        System.Diagnostics.Debug.WriteLine($"[GetAzaDatesFromUI] Total AZA dates: {azaDates.Count}");
        return azaDates;
    }

    /// <summary>
    /// Gets the display name for the current user from settings.
    /// Prefers Vorname + Nachname if available, falls back to Name for backward compatibility.
    /// </summary>
    public string? SettingsName
    {
        get
        {
            if (_settings == null) return null;
            
            // Use Vorname + Nachname when beide vorhanden
            if (!string.IsNullOrWhiteSpace(_settings.Vorname) && !string.IsNullOrWhiteSpace(_settings.Nachname))
            {
                return $"{_settings.Vorname} {_settings.Nachname}".Trim();
            }

            // Wenn nur Nachname gesetzt ist
            if (string.IsNullOrWhiteSpace(_settings.Vorname) && !string.IsNullOrWhiteSpace(_settings.Nachname))
            {
                return _settings.Nachname;
            }

            // Fallback: Vorname allein oder Legacy Name
            if (!string.IsNullOrWhiteSpace(_settings.Vorname))
            {
                return _settings.Vorname;
            }

            // Fallback auf legacy Name
            return _settings.Name;
        }
    }
    public string? SettingsAbteilung => _settings?.Abteilung;

    /// <summary>
    /// Initialize by loading settings
    /// </summary>
    public async System.Threading.Tasks.Task InitializeAsync()
    {
        _currentRequestId = null;
        _settings = await _settingsService.LoadAsync();
        
        if (_settings == null)
        {
            // Should not happen if app flow is correct (wizard should have created settings)
            ErrorMessage = "Keine Einstellungen gefunden. Bitte starten Sie die Anwendung neu.";
            HasErrors = true;
            return;
        }

        OnPropertyChanged(nameof(SettingsName));
        OnPropertyChanged(nameof(SettingsAbteilung));
        OnPropertyChanged(nameof(AnnualEntitlement));

        // Load approved days from history for current year
        try
        {
            var historyEntries = await _historyService.GetEntriesAsync(Year, StatusFilter.Approved);
            ApprovedDays = historyEntries.Sum(e => e.CalculatedDays);
            _logger.LogInformation("Loaded {Count} approved entries totaling {Days} days",
                historyEntries.Count, ApprovedDays);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load approved days from history");
            ApprovedDays = 0;
        }

        // Try to update school holidays automatically if student mode is active
        if (_settings.StudentActive && !string.IsNullOrEmpty(_settings.Bundesland))
        {
            _ = UpdateSchoolHolidaysAsync(_settings.Bundesland);
        }

        RemainingDays = _settings.Jahresurlaub - ApprovedDays;

        // Load all history entries for current year
        await LoadHistoryEntriesAsync();

        Calculate();
    }

    public async Task<IReadOnlyCollection<DateOnly>> GetApprovedVacationDatesAsync()
    {
        try
        {
            var approvedEntries = await _historyService.GetEntriesAsync(Year, StatusFilter.Approved);
            var dates = new HashSet<DateOnly>();

            foreach (var entry in approvedEntries)
            {
                for (var date = entry.StartDate; date <= entry.EndDate; date = date.AddDays(1))
                {
                    dates.Add(date);
                }
            }

            return dates;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load approved vacation dates");
            return Array.Empty<DateOnly>();
        }
    }

    private async Task UpdateSchoolHolidaysAsync(string bundesland)
    {
        try
        {
            var year = DateTime.Today.Year;
            var service = new OnlineHolidayService();
            var cachePath = Path.Combine(_pathService.GetAppDataDirectory(), "school_holidays_cache.json");
            
            // Ensure directory exists
            var dir = Path.GetDirectoryName(cachePath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Update local cache
            // We fetch for current year. Ideally we might want next year too.
            var updated = await service.FetchAndCacheAsync(bundesland, year, cachePath);
            // Also fetch next year to be safe for cross-year planning
            var updatedNext = await service.FetchAndCacheAsync(bundesland, year + 1, cachePath);
            
            if (updated || updatedNext)
            {
                // Reload provider with new cache
                _schoolHolidayProvider.Reload(cachePath);
                
                // Re-calculate to apply new holidays
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Calculate());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update school holidays");
        }
    }

    /// <summary>
    /// Calculate vacation request
    /// </summary>
    public void Calculate()
    {
        if (_settings == null)
        {
            return;
        }
        // Ensure AZA days are within the current range before building the request
        CleanAzaDays();

        // Run lightweight field validation before performing domain calculation
        ValidateFields();

        // If there are field-level validation errors, skip calculation and let UI focus the field
        if (HasErrors)
        {
            RequestedDays = 0;
            return;
        }

        try
        {
            // Map NRW to NW for internal logic
            var bundesland = _settings.Bundesland ?? "NW";
            if (bundesland == "NRW")
            {
                bundesland = "NW";
            }

            var studentParams = new StudentParameters(
                _settings.StudentActive,
                bundesland,
                _settings.VocationalSchool
            );

            var request = new VacationRequest(
                StartDate,
                EndDate,
                StartHalfDay,
                EndHalfDay,
                _settings.Workdays,
                studentParams,
                bundesland,
                _settings.Jahresurlaub,
                ApprovedDays,
                Year,
                GetAzaDatesFromUI()
            );

            _lastResult = _calculator.Calculate(request);

            if (_lastResult.HasErrors)
            {
                ErrorMessage = string.Join("\n", _lastResult.Errors);
                HasErrors = true;
                RequestedDays = 0;
            }
            else
            {
                ErrorMessage = string.Empty;
                HasErrors = false;
                RequestedDays = _lastResult.TotalDays;
            }

            // Berechne Resturlaub für Anzeige (OHNE aktuellen Antrag abzuziehen, nur genehmigte + offene Historie)
            // User request: "bei der berechnung der verbleibenden urlaubstage, ziehe bitte nur die bereits genehmigten tage vom resturlaub ab."
            RemainingDays = _settings.Jahresurlaub - ApprovedDays;

            // Effektiven Resturlaub für Farbgebung berechnen (inkl. aktueller Anfrage)
            var effectiveBalance = RemainingDays - PendingHistoryDays - RequestedDays;
            
            // Färbung basierend auf effektivem Resturlaub
            if (effectiveBalance < 0)
            {
                RemainingDaysColor = new SolidColorBrush(Color.Parse("#F44336")); // Rot
            }
            else if (effectiveBalance <= 5)
            {
                RemainingDaysColor = new SolidColorBrush(Color.Parse("#FFC107")); // Gelb
            }
            else
            {
                RemainingDaysColor = new SolidColorBrush(Color.Parse("#E5EEF9")); // Blau für >5 Tage
            }

            // Update the detailed day breakdown so the UI can explain the calculation
            UpdateBreakdown();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Fehler bei der Berechnung: {ex.Message}";
            HasErrors = true;
        }
    }

    /// <summary>
    /// Adds a single AZA date with validation (duplicates / range checks).
    /// This method centralizes AZA insertion logic for the new single-picker UX.
    /// </summary>
    /// <param name="date">Date to add as AZA</param>
    public void AddAzaDate(DateOnly date)
    {
        // Enable AZA mode if user adds a date
        if (!HasAzaDays)
        {
            HasAzaDays = true;
        }

        // Validate range
        if (date < StartDate || date > EndDate)
        {
            ErrorMessage = $"AZA-Tage müssen innerhalb des gewählten Zeitraums liegen: {StartDate:dd.MM.yyyy} - {EndDate:dd.MM.yyyy}";
            HasErrors = true;
            return;
        }

        // Prevent duplicates
        var exists = AzaDays.Any(item => item.Date.HasValue && DateOnly.FromDateTime(item.Date.Value.LocalDateTime) == date);
        if (exists)
        {
            ErrorMessage = "Dieses Datum ist bereits als AZA-Tag vorhanden.";
            HasErrors = true;
            return;
        }

        // Create and add the AzaDayItem
        var item = new AzaDayItem(RemoveAzaDay)
        {
            // Store as UTC (Offset.Zero) to avoid timezone-related date shifts
            Date = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
        };
        item.PropertyChanged += OnAzaDayPropertyChanged;
        AzaDays.Add(item);

        // Clear any previous errors caused by add attempts
        ErrorMessage = string.Empty;
        HasErrors = false;

        // Re-calculate
        Calculate();
    }

    /// <summary>
    /// Perform lightweight per-field validation and populate field-level error properties.
    /// This keeps validation close to controls and avoids surfacing everything as a global error.
    /// </summary>
    private void ValidateFields()
    {
        // Clear prior field-level errors
        StartDateError = string.Empty;
        EndDateError = string.Empty;

        var errors = new List<string>();

        if (EndDate < StartDate)
        {
            EndDateError = "Enddatum muss gleich oder nach dem Startdatum liegen.";
            errors.Add(EndDateError);
        }

        // Add further field-level checks here (half-day rules, required settings etc.)

        if (errors.Count > 0)
        {
            ErrorMessage = string.Join("\n", errors);
            HasErrors = true;
        }
        else
        {
            // Clear global error state for field-level only checks
            ErrorMessage = string.Empty;
            HasErrors = false;
        }
    }

    /// <summary>
    /// Remove any AZA entries that are outside the current StartDate..EndDate range.
    /// Returns the number removed so callers can inform the user if needed.
    /// </summary>
    private int CleanAzaDays()
    {
        var toRemove = AzaDays.Where(a => !a.Date.HasValue || DateOnly.FromDateTime(a.Date!.Value.LocalDateTime) < StartDate || DateOnly.FromDateTime(a.Date!.Value.LocalDateTime) > EndDate).ToList();
        foreach (var item in toRemove)
        {
            item.PropertyChanged -= OnAzaDayPropertyChanged;
            AzaDays.Remove(item);
        }

        if (toRemove.Count > 0)
        {
            // Informational message (not an error) so UI can show a gentle hint if desired
            ErrorMessage = $"Info: {toRemove.Count} AZA-Tage entfernt, lagen außerhalb des gewählten Zeitraums.";
            // Keep HasErrors false
            HasErrors = false;
        }

        return toRemove.Count;
    }

    /// <summary>
    /// Export to PDF via template filling (async)
    /// </summary>
    public async System.Threading.Tasks.Task<string?> ExportPdfAsync()
    {
        IsBusy = true;
        if (_settings == null)
        {
            ErrorMessage = "❌ Keine Einstellungen vorhanden. Bitte öffnen Sie die Einstellungen und füllen Sie alle Pflichtfelder aus.";
            HasErrors = true;
            IsBusy = false;
            return null;
        }

        if (_lastResult == null)
        {
            ErrorMessage = "❌ Keine Berechnung vorhanden. Bitte wählen Sie einen Zeitraum aus.";
            HasErrors = true;
            return null;
        }

        if (HasErrors)
        {
            ErrorMessage = $"❌ Export nicht möglich: {ErrorMessage}";
            return null;
        }

        // Check if resturlaub would be depleted (Validation Logic)
        var effectiveBalance = (_settings.Jahresurlaub - ApprovedDays) - PendingHistoryDays - RequestedDays;

        if (effectiveBalance < 0)
        {
            ErrorMessage = $"❌ Kein Resturlaub mehr vorhanden!\n\nJahresurlaub: {_settings.Jahresurlaub} Tage\nGenehmigt: {ApprovedDays} Tage\nBeantragt: {RequestedDays} Tage\nResturlaub (nach Abzug): {effectiveBalance} Tage\n\n→ Sie können keinen weiteren Urlaub nehmen.";
            HasErrors = true;
            return null;
        }

        try
        {
            // Map NRW to NW for internal logic
            var bundesland = _settings.Bundesland ?? "NW";
            if (bundesland == "NRW")
            {
                bundesland = "NW";
            }

            var request = new VacationRequest(
                StartDate,
                EndDate,
                StartHalfDay,
                EndHalfDay,
                _settings.Workdays,
                new StudentParameters(_settings.StudentActive, bundesland, _settings.VocationalSchool),
                bundesland,
                _settings.Jahresurlaub,
                ApprovedDays,
                Year,
                GetAzaDatesFromUI()
            );

            // Step 1: Validate all required fields are populated
            var resolver = new PlaceholderResolver(_settings, request, _lastResult);
            var validationErrors = resolver.ValidateRequiredFields();
            if (validationErrors.Count > 0)
            {
                ErrorMessage = $"❌ Fehlende Pflichtfelder:\n{string.Join("\n", validationErrors.Select(e => "  • " + e))}\n\n→ Bitte öffnen Sie die Einstellungen und vervollständigen Sie Ihre Angaben.";
                HasErrors = true;
                return null;
            }

            // Step 2: Resolve all template field values (German-formatted)
            TemplateFieldValues fieldValues;
            try
            {
                fieldValues = resolver.ResolvePlaceholders();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"❌ Fehler beim Vorbereiten der Daten:\n{ex.Message}\n\n→ Bitte überprüfen Sie Ihre Einstellungen.";
                HasErrors = true;
                return null;
            }

            var exportDir = string.IsNullOrWhiteSpace(_settings?.ExportPath) 
                ? _pathService.GetExportDirectory() 
                : _settings.ExportPath;
            
            // Step 2.5: Validate export directory
            try
            {
                Directory.CreateDirectory(exportDir);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"❌ Fehler beim Erstellen des Export-Ordners:\n{exportDir}\n\nFehler: {ex.Message}\n\n→ Bitte überprüfen Sie die Berechtigungen oder ändern Sie den Export-Pfad in den Einstellungen.";
                HasErrors = true;
                return null;
            }

            // Step 3: Create PDF by filling AcroForm fields (no coordinate stamping)
            string pdfPath;
            try
            {
                pdfPath = await _pdfFormFillService.CreateFilledPdfAsync(fieldValues, StartDate.ToDateTime(TimeOnly.MinValue), EndDate.ToDateTime(TimeOnly.MinValue), true, exportDir);
            }
            catch (AggregateException aggEx)
            {
                var innermost = GetInnermostException(aggEx);
                var exceptionTypeName = innermost.GetType().Name;
                var errorDetails = innermost.Message;
                var errorType = "";

                // Log full exception for debugging
                System.Diagnostics.Debug.WriteLine($"PDF Export Error - Type: {exceptionTypeName}");
                System.Diagnostics.Debug.WriteLine($"Message: {innermost.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack: {innermost.StackTrace}");

                if (innermost is FileNotFoundException)
                {
                    errorType = "PDF-Template nicht gefunden";
                    errorDetails = $"Das PDF-Template konnte nicht geladen werden.\n{innermost.Message}\n\n→ Bitte installieren Sie die App neu.";
                }
                else if (innermost is UnauthorizedAccessException)
                {
                    errorType = "Keine Schreibberechtigung";
                    errorDetails = $"Die App hat keine Berechtigung, in diesen Ordner zu schreiben:\n{exportDir}\n\n→ Bitte ändern Sie den Export-Pfad in den Einstellungen oder prüfen Sie die Ordnerberechtigungen.";
                }
                else if (innermost is IOException)
                {
                    errorType = "Dateizugriffsfehler";
                    errorDetails = $"{innermost.Message}\n\n→ Möglicherweise ist der Ordner nicht verfügbar oder eine andere App blockiert den Zugriff.";
                }
                else if (exceptionTypeName.Contains("Pdf"))
                {
                    // iText PDF exception - show more details
                    errorType = $"PDF-Bibliothek-Fehler ({exceptionTypeName})";
                    errorDetails = $"{innermost.Message}\n\nTechnische Details:\n{innermost.GetType().FullName}\n\n→ Möglicherweise ist das Template beschädigt oder die PDF-Bibliothek hat ein Problem. Bitte installieren Sie die App neu.";
                }
                else
                {
                    errorType = $"PDF-Generierung fehlgeschlagen ({exceptionTypeName})";
                    errorDetails = $"{innermost.Message}\n\nFehlertyp: {innermost.GetType().FullName}\n\n→ Bitte versuchen Sie es erneut.";
                }

                ErrorMessage = $"❌ {errorType}:\n{errorDetails}";
                HasErrors = true;
                return null;
            }
            catch (Exception ex)
            {
                var exceptionTypeName = ex.GetType().Name;
                
                // Log for debugging
                System.Diagnostics.Debug.WriteLine($"PDF Export Error (Non-Aggregate) - Type: {exceptionTypeName}");
                System.Diagnostics.Debug.WriteLine($"Message: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack: {ex.StackTrace}");
                
                var innermost = GetInnermostException(ex);
                ErrorMessage = $"❌ Unerwarteter Fehler beim PDF-Export:\n\nFehlertyp: {innermost.GetType().FullName}\n\nMeldung: {innermost.Message}\n\n→ Bitte versuchen Sie es erneut oder kontaktieren Sie den Support.";
                HasErrors = true;
                return null;
            }

            // Step 4: Save to history
            try
            {
                // Create or update history entry
                if (_currentRequestId == null)
                {
                    var dto = new CreateVacationRequestDto(
                        Year: Year,
                        StartDate: StartDate,
                        EndDate: EndDate,
                        StartHalfDay: StartHalfDay,
                        EndHalfDay: EndHalfDay,
                        CalculatedDays: RequestedDays,
                        AzaDates: GetAzaDatesFromUI());

                    _currentRequestId = await _historyService.CreateAsync(dto);
                    _logger.LogInformation("Created history request {RequestId}", _currentRequestId);
                }

                // Mark as exported with PDF path
                await _historyService.MarkExportedAsync(_currentRequestId.Value, pdfPath);
                _logger.LogInformation("Marked request {RequestId} as exported: {PdfPath}", _currentRequestId, pdfPath);
            }
            catch (Exception historyEx)
            {
                // PDF was created successfully, but history save failed - warn user but return PDF path
                ErrorMessage = $"⚠️ PDF wurde erstellt, aber der Eintrag konnte nicht in der Historie gespeichert werden:\n{historyEx.Message}\n\nPDF wurde trotzdem erstellt: {Path.GetFileName(pdfPath)}";
                HasErrors = true;
                _logger.LogError(historyEx, "Failed to save to history");
            }

            // Clear any previous errors if we reached this point
            if (!HasErrors)
            {
                ErrorMessage = string.Empty;
            }

            return pdfPath;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"❌ Unerwarteter Fehler beim Export:\n{ex.Message}\n\n→ Bitte versuchen Sie es erneut.";
            HasErrors = true;
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Gets the innermost exception from an aggregate or nested exception chain.
    /// </summary>
    private static Exception GetInnermostException(Exception ex)
    {
        while (ex.InnerException != null)
        {
            ex = ex.InnerException;
        }

        if (ex is AggregateException aggEx && aggEx.InnerExceptions.Count > 0)
        {
            return GetInnermostException(aggEx.InnerExceptions[0]);
        }

        return ex;
    }

    /// <summary>
    /// Reload settings (e.g., after settings window closes)
    /// </summary>
    public async System.Threading.Tasks.Task ReloadSettingsAsync()
    {
        await InitializeAsync();
    }

    private void UpdateBreakdown()
    {
        Breakdown.Clear();

        if (_lastResult == null || _settings == null)
        {
            return;
        }

        var current = StartDate;
        while (current <= EndDate)
        {
            var isWorkday = _settings.Workdays.Contains(current.DayOfWeek);
            var dayCount = 0m;
            var badges = new List<string>();

            if (!isWorkday)
            {
                badges.Add("Kein Arbeitstag");
            }
            else
            {
                dayCount = 1m;

                // Check for public holiday
                var publicHolidayProvider = new PublicHolidayProvider();
                if (publicHolidayProvider.IsPublicHoliday(current, _settings.Bundesland ?? "NW"))
                {
                    dayCount = 0m;
                    badges.Add("Feiertag");
                }

                // Check for school holiday (if student mode)
                if (_settings.StudentActive)
                {
                    var schoolHolidayProvider = new SchoolHolidayProvider();
                    if (schoolHolidayProvider.IsSchoolHoliday(current, _settings.Bundesland ?? "NW"))
                    {
                        badges.Add("Schulferien");
                    }
                    else
                    {
                        // Check vocational school
                        var vocType = _settings.VocationalSchool.GetValueOrDefault(current.DayOfWeek, VocationalSchoolDayType.None);
                        if (vocType == VocationalSchoolDayType.Full)
                        {
                            badges.Add("Berufsschule (Ganztag)");
                        }
                        else if (vocType == VocationalSchoolDayType.Half)
                        {
                            dayCount = 0.5m;
                            badges.Add("Berufsschule (Halbtag)");
                        }
                    }
                }

                // Apply half-day rules
                if (current == StartDate && StartHalfDay && dayCount > 0)
                {
                    dayCount = Math.Min(dayCount, 0.5m);
                    if (!badges.Contains("Berufsschule (Halbtag)"))
                    {
                        badges.Add("Halber Tag (Start)");
                    }
                }
                else if (current == EndDate && EndHalfDay && dayCount > 0)
                {
                    dayCount = Math.Min(dayCount, 0.5m);
                    if (!badges.Contains("Berufsschule (Halbtag)"))
                    {
                        badges.Add("Halber Tag (Ende)");
                    }
                }
            }

            Breakdown.Add(new DayBreakdownItem
            {
                Date = current,
                DayOfWeek = GetGermanDayName(current.DayOfWeek),
                Days = dayCount,
                Badges = string.Join(", ", badges)
            });

            current = current.AddDays(1);
        }
    }

    private string GetGermanDayName(DayOfWeek dayOfWeek)
    {
        return dayOfWeek switch
        {
            DayOfWeek.Monday => "Montag",
            DayOfWeek.Tuesday => "Dienstag",
            DayOfWeek.Wednesday => "Mittwoch",
            DayOfWeek.Thursday => "Donnerstag",
            DayOfWeek.Friday => "Freitag",
            DayOfWeek.Saturday => "Samstag",
            DayOfWeek.Sunday => "Sonntag",
            _ => dayOfWeek.ToString()
        };
    }

    /// <summary>
    /// Load all vacation requests for the current year into the history grid.
    /// </summary>
    private async System.Threading.Tasks.Task LoadHistoryEntriesAsync()
    {
        try
        {
            var filter = _historyFilterStatusIndex switch
            {
                1 => StatusFilter.Exported,
                2 => StatusFilter.Approved,
                3 => StatusFilter.Rejected,
                4 => StatusFilter.Archived,
                _ => StatusFilter.All
            };
            
            // Get all entries for the selected year and filter
            var historyEntries = await _historyService.GetEntriesAsync(_historyFilterYear, filter);
            
            // Calculate pending days (Exported but not Approved) - ALWAYS across current year regardless of filter
            // Re-fetch for calculation if filter is restrictive, or just rely on main history view being comprehensive?
            // Actually, pending days should probably just track "Active" requests.
            // For now, let's keep the pending calculation based on the *loaded* entries if filter is compatible,
            // or maybe we should fetch ALL for the year separately to ensure calculation is correct.
            var allEntriesForYear = await _historyService.GetEntriesAsync(Year, StatusFilter.All);
            PendingHistoryDays = allEntriesForYear
                .Where(e => e.Status == VacationRequestStatus.Exported || e.Status == VacationRequestStatus.Draft)
                .Sum(e => e.CalculatedDays);

            HistoryEntries.Clear();

            var sortedEntries = _historySortIndex switch 
            {
                0 => historyEntries.OrderBy(e => e.StartDate),               // Datum ⬆
                1 => historyEntries.OrderByDescending(e => e.StartDate),     // Datum ⬇
                2 => historyEntries.OrderByDescending(e => e.CreatedAt),     // Erstellt ⬇
                3 => historyEntries.OrderBy(e => e.Status),                  // Status
                _ => historyEntries.OrderBy(e => e.StartDate)
            };

            foreach (var entry in sortedEntries)
            {
                var viewModel = new MainHistoryEntryViewModel(
                    entry,
                    onApprove: (requestId) => ApproveRequestAsync(requestId),
                    onDelete: (requestId) => DeleteRequestAsync(requestId),
                    onArchive: (requestId) => ArchiveRequestAsync(requestId),
                    onReject: async (requestId, reason) => await RejectRequestAsync(requestId, reason)
                );
                HistoryEntries.Add(viewModel);
            }
            
            _logger.LogInformation("Loaded {Count} history entries for year {Year}", HistoryEntries.Count, _historyFilterYear);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load history entries");
        }
    }

    /// <summary>
    /// Approve a vacation request.
    /// </summary>
    private async System.Threading.Tasks.Task ApproveRequestAsync(Guid requestId)
    {
        try
        {
            await _historyService.MarkApprovedAsync(requestId);
            _logger.LogInformation("Approved request {RequestId}", requestId);
            
            // Reload history and recalculate
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to approve request {RequestId}", requestId);
            ErrorMessage = $"Fehler beim Genehmigen: {ex.Message}";
            HasErrors = true;
        }
    }

    /// <summary>
    /// Archive a vacation request.
    /// </summary>
    private async System.Threading.Tasks.Task ArchiveRequestAsync(Guid requestId)
    {
        try
        {
            await _historyService.MarkArchivedAsync(requestId);
            _logger.LogInformation("Archived request {RequestId}", requestId);
            
            // Reload history and recalculate
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to archive request {RequestId}", requestId);
            ErrorMessage = $"Fehler beim Archivieren: {ex.Message}";
            HasErrors = true;
        }
    }

    /// <summary>
    /// Reject a vacation request with a provided reason.
    /// </summary>
    private async System.Threading.Tasks.Task RejectRequestAsync(Guid requestId, string reason)
    {
        try
        {
            await _historyService.MarkRejectedAsync(requestId, reason);
            _logger.LogInformation("Rejected request {RequestId} with reason: {Reason}", requestId, reason);

            // Reload history and recalculate
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reject request {RequestId}", requestId);
            ErrorMessage = $"Fehler beim Ablehnen: {ex.Message}";
            HasErrors = true;
        }
    }

    /// <summary>
    /// Delete a vacation request.
    /// </summary>
    private async System.Threading.Tasks.Task DeleteRequestAsync(Guid requestId)
    {
        try
        {
            await _historyService.MarkDeletedAsync(requestId);
            _logger.LogInformation("Deleted request {RequestId}", requestId);
            
            // Reload history and recalculate
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete request {RequestId}", requestId);
            ErrorMessage = $"Fehler beim Löschen: {ex.Message}";
            HasErrors = true;
        }
    }
}

/// <summary>
/// Item for the day breakdown list
/// </summary>
public class DayBreakdownItem
{
    public DateOnly Date { get; set; }
    public string DayOfWeek { get; set; } = string.Empty;
    public decimal Days { get; set; }
    public string Badges { get; set; } = string.Empty;
    public string DateString => Date.ToString("dd.MM.yyyy");
    public string DaysString => Days.ToString("0.0");
}
