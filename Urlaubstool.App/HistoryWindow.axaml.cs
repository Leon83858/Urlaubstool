using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.History;
using Urlaubstool.Infrastructure.Logging;
using Urlaubstool.Infrastructure.Paths;

namespace Urlaubstool.App;

public partial class HistoryWindow : Window
{
    private readonly IHistoryService _historyService;
    private readonly ILogger<HistoryWindow> _logger;
    private readonly ObservableCollection<HistoryEntryViewModel> _entries;
    private readonly PathService _pathService;
    private readonly int _initialYear;
    private HistoryEntryViewModel? _selectedEntry;

    public HistoryWindow() : this(DateTime.Today.Year)
    {
    }

    public HistoryWindow(int initialYear)
    {
        InitializeComponent();
        _initialYear = initialYear;
        
        _pathService = new PathService();
        _logger = new ConsoleLogger<HistoryWindow>();
        var storeLogger = new ConsoleLogger<JsonlHistoryStore>();
        var serviceLogger = new ConsoleLogger<HistoryService>();
        
        var historyStore = new JsonlHistoryStore(_pathService, storeLogger);
        _historyService = new HistoryService(historyStore, serviceLogger);
        
        _entries = new ObservableCollection<HistoryEntryViewModel>();

        // Setup UI
        Loaded += OnLoaded;
        
        // Setup event handlers
        var yearFilter = this.FindControl<NumericUpDown>("YearFilter")!;
        var statusFilter = this.FindControl<ComboBox>("StatusFilter")!;
        var dataGrid = this.FindControl<DataGrid>("EntriesDataGrid")!;

        yearFilter.ValueChanged += (s, e) => LoadEntries();
        statusFilter.SelectionChanged += (s, e) => LoadEntries();
        dataGrid.SelectionChanged += OnSelectionChanged;
        
        // Set ItemsSource
        dataGrid.ItemsSource = _entries;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _logger.LogInformation("HistoryWindow loaded");
        
        var yearFilter = this.FindControl<NumericUpDown>("YearFilter")!;
        yearFilter.Value = _initialYear;
        
        // Load entries
        LoadEntries();
    }

    private async void LoadEntries()
    {
        try
        {
            var yearFilter = this.FindControl<NumericUpDown>("YearFilter")!;
            var statusFilter = this.FindControl<ComboBox>("StatusFilter")!;
            
            int year = (int)(yearFilter.Value ?? DateTime.Today.Year);
            string? statusText = (statusFilter.SelectedItem as ComboBoxItem)?.Content?.ToString();
            
            _logger.LogInformation("=== HISTORYWINDOW DEBUG ===");
            _logger.LogInformation("History path: {Path}", _pathService.GetHistoryFilePath());
            _logger.LogInformation("Loading entries for year={Year}, status={Status}", year, statusText);
            _logger.LogInformation("StatusFilter.SelectedIndex={Index}, SelectedItem={Item}", 
                statusFilter.SelectedIndex, statusFilter.SelectedItem);

            var filter = statusText switch
            {
                "Exportiert" => Domain.StatusFilter.Exported,
                "Genehmigt" => Domain.StatusFilter.Approved,
                "Abgelehnt" => Domain.StatusFilter.Rejected,
                "Archiviert" => Domain.StatusFilter.Archived,
                _ => Domain.StatusFilter.All
            };

            _logger.LogInformation("Using filter: {Filter}", filter);

            var entries = await _historyService.GetEntriesAsync(year, filter);
            
            _logger.LogInformation("Loaded {Count} entries from service", entries.Count);
            foreach (var entry in entries)
            {
                _logger.LogInformation("Entry: {Id}, Status={Status}, Date={Start}-{End}", 
                    entry.RequestId, entry.Status, entry.StartDate, entry.EndDate);
            }

            _entries.Clear();
            foreach (var entry in entries)
            {
                _entries.Add(new HistoryEntryViewModel(entry));
            }
            
            _logger.LogInformation("ObservableCollection now has {Count} items", _entries.Count);
            
            if (entries.Count == 0)
            {
                await ShowInfo("Keine Einträge", $"Keine Einträge für Jahr {year} mit Filter '{statusText}'.\n\nPrüfe:\n- Jahr-Filter\n- Status-Filter\n- history.jsonl enthält Events");
            }
            
            UpdateButtonStates();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load entries");
            await ShowError("Fehler beim Laden", ex.Message);
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var dataGrid = this.FindControl<DataGrid>("EntriesDataGrid")!;
        _selectedEntry = dataGrid.SelectedItem as HistoryEntryViewModel;
        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        var openPdfButton = this.FindControl<Button>("OpenPdfButton")!;
        var approveButton = this.FindControl<Button>("ApproveButton")!;
        var rejectButton = this.FindControl<Button>("RejectButton")!;
        var archiveButton = this.FindControl<Button>("ArchiveButton")!;

        bool hasSelection = _selectedEntry != null;
        bool hasPdf = hasSelection && !string.IsNullOrEmpty(_selectedEntry?.Entry.PdfPath);
        bool canApprove = hasSelection && _selectedEntry?.Entry.Status == VacationRequestStatus.Exported;
        bool canReject = hasSelection && _selectedEntry?.Entry.Status == VacationRequestStatus.Exported;
        bool canArchive = hasSelection && 
            (_selectedEntry?.Entry.Status == VacationRequestStatus.Approved ||
             _selectedEntry?.Entry.Status == VacationRequestStatus.Rejected);

        openPdfButton.IsEnabled = hasPdf;
        approveButton.IsEnabled = canApprove;
        rejectButton.IsEnabled = canReject;
        archiveButton.IsEnabled = canArchive;
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        LoadEntries();
    }

    private async void OpenPdf_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedEntry == null || string.IsNullOrEmpty(_selectedEntry.Entry.PdfPath))
            return;

        try
        {
            var pdfPath = _selectedEntry.Entry.PdfPath;
            
            if (!File.Exists(pdfPath))
            {
                await ShowError("PDF nicht gefunden", $"Die Datei existiert nicht:\n{pdfPath}");
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(pdfPath) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", pdfPath);
            }
            else
            {
                Process.Start("xdg-open", pdfPath);
            }
            
            _logger.LogInformation("Opened PDF: {Path}", pdfPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open PDF");
            await ShowError("Fehler beim Öffnen", ex.Message);
        }
    }

    private async void Approve_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedEntry == null)
            return;

        try
        {
            await _historyService.MarkApprovedAsync(_selectedEntry.Entry.RequestId);
            _logger.LogInformation("Approved request {RequestId}", _selectedEntry.Entry.RequestId);
            LoadEntries();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to approve request");
            await ShowError("Fehler beim Genehmigen", ex.Message);
        }
    }

    private async void Reject_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedEntry == null)
            return;

        var reason = await ShowInputDialog("Ablehnungsgrund", "Bitte geben Sie einen Grund ein:");
        if (string.IsNullOrWhiteSpace(reason))
            return;

        try
        {
            await _historyService.MarkRejectedAsync(_selectedEntry.Entry.RequestId, reason);
            _logger.LogInformation("Rejected request {RequestId}", _selectedEntry.Entry.RequestId);
            LoadEntries();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reject request");
            await ShowError("Fehler beim Ablehnen", ex.Message);
        }
    }

    private async void Archive_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedEntry == null)
            return;

        try
        {
            await _historyService.MarkArchivedAsync(_selectedEntry.Entry.RequestId);
            _logger.LogInformation("Archived request {RequestId}", _selectedEntry.Entry.RequestId);
            LoadEntries();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to archive request");
            await ShowError("Fehler beim Archivieren", ex.Message);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async System.Threading.Tasks.Task ShowError(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var panel = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 15 };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });

        var button = new Button
        {
            Content = "OK",
            Width = 100,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        button.Click += (s, e) => dialog.Close();
        panel.Children.Add(button);

        dialog.Content = panel;
        await dialog.ShowDialog(this);
    }

    private async System.Threading.Tasks.Task ShowInfo(string title, string message)
    {
        await ShowError(title, message);
    }

    private async System.Threading.Tasks.Task<string?> ShowInputDialog(string title, string prompt)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var panel = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 15 };
        panel.Children.Add(new TextBlock { Text = prompt });

        var textBox = new TextBox { Width = 360 };
        panel.Children.Add(textBox);

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10
        };

        string? result = null;

        var okButton = new Button { Content = "OK", Width = 80 };
        okButton.Click += (s, e) =>
        {
            result = textBox.Text;
            dialog.Close();
        };

        var cancelButton = new Button { Content = "Abbrechen", Width = 80 };
        cancelButton.Click += (s, e) => dialog.Close();

        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        panel.Children.Add(buttonPanel);

        dialog.Content = panel;
        await dialog.ShowDialog(this);

        return result;
    }
}

/// <summary>
/// ViewModel for displaying history entries in the DataGrid
/// </summary>
public class HistoryEntryViewModel
{
    public HistoryEntry Entry { get; }

    public HistoryEntryViewModel(HistoryEntry entry)
    {
        Entry = entry;
    }

    public string CreatedAtString => Entry.CreatedAt.ToString("dd.MM.yyyy HH:mm");
    public string StartDateString => Entry.StartDate.ToString("dd.MM.yyyy");
    public string EndDateString => Entry.EndDate.ToString("dd.MM.yyyy");
    public string DaysString => Entry.CalculatedDays.ToString("F1");
    public string StatusString => Entry.Status switch
    {
        VacationRequestStatus.Draft => "Entwurf",
        VacationRequestStatus.Exported => "Exportiert",
        VacationRequestStatus.Approved => "Genehmigt",
        VacationRequestStatus.Rejected => "Abgelehnt",
        VacationRequestStatus.Archived => "Archiviert",
        VacationRequestStatus.Deleted => "Gelöscht",
        _ => Entry.Status.ToString()
    };
    public string PdfFileName => string.IsNullOrEmpty(Entry.PdfPath) 
        ? "-" 
        : Path.GetFileName(Entry.PdfPath);
}
