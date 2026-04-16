using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Urlaubstool.App.ViewModels;
using Urlaubstool.Domain;

namespace Urlaubstool.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        Console.WriteLine("[DEBUG] MainWindow.ctor() called");
        InitializeComponent();
        
        Console.WriteLine("[DEBUG] InitializeComponent() completed");
        
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;

        Loaded += MainWindow_Loaded;
        SetupUI();
        
        Console.WriteLine("[DEBUG] MainWindow.ctor() completed");
    }

    protected override void OnOpened(EventArgs e)
    {
        Console.WriteLine("[DEBUG] MainWindow.OnOpened()");
        base.OnOpened(e);
    }

    private async void MainWindow_Loaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
        UpdateUI();
    }

    private void SetupUI()
    {
        // Set initial values
        var today = DateTime.Today;
        var startDateButton = this.FindControl<Button>("StartDateButton")!;
        var endDateButton = this.FindControl<Button>("EndDateButton")!;
        var startHalfDayCheckBox = this.FindControl<CheckBox>("StartHalfDayCheckBox")!;
        var endHalfDayCheckBox = this.FindControl<CheckBox>("EndHalfDayCheckBox")!;
        var historyDataGrid = this.FindControl<DataGrid>("HistoryDataGrid")!;
        
        // Initialize with current dates
        var startDate = DateOnly.FromDateTime(today);
        var endDate = DateOnly.FromDateTime(today.AddDays(4));
        
        _viewModel.StartDate = startDate;
        _viewModel.EndDate = endDate;
        
        UpdateDateDisplay();

        startHalfDayCheckBox.IsCheckedChanged += (s, e) =>
        {
            _viewModel.StartHalfDay = startHalfDayCheckBox.IsChecked == true;
            UpdateUI();
        };

        endHalfDayCheckBox.IsCheckedChanged += (s, e) =>
        {
            _viewModel.EndHalfDay = endHalfDayCheckBox.IsChecked == true;
            UpdateUI();
        };

        // Bind history DataGrid
        historyDataGrid.ItemsSource = _viewModel.HistoryEntries;

        // Attach confirm/reason callbacks for history entry view models so UI can prompt users
        void AttachHistoryCallbacks(MainHistoryEntryViewModel vm)
        {
            // Confirm delete callback
            vm.ConfirmDeleteAsync = async () =>
            {
                return await ShowConfirmDialog("Löschen bestätigen", "Soll der Eintrag wirklich gelöscht werden?");
            };

            // Request rejection reason callback
            vm.RequestRejectionReasonAsync = async () =>
            {
                return await ShowInputDialog("Ablehnen", "Bitte geben Sie einen Ablehnungsgrund ein:");
            };
        }

        // Attach callbacks for existing items
        foreach (var item in _viewModel.HistoryEntries)
        {
            AttachHistoryCallbacks(item);
        }

        // Attach for future items
        _viewModel.HistoryEntries.CollectionChanged += (s, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (var ni in e.NewItems)
                {
                    if (ni is MainHistoryEntryViewModel newVm)
                    {
                        AttachHistoryCallbacks(newVm);
                    }
                }
            }
        };

        // Setup AZA days UI
        var hasAzaDaysCheckBox = this.FindControl<CheckBox>("HasAzaDaysCheckBox")!;
        var azaDaysPanel = this.FindControl<StackPanel>("AzaDaysPanel")!;
        var azaDaysItemsControl = this.FindControl<ItemsControl>("AzaDaysItemsControl")!;
        // Initialize checkbox and panel visibility from ViewModel
        hasAzaDaysCheckBox.IsChecked = _viewModel.HasAzaDays;
        azaDaysPanel.IsVisible = _viewModel.HasAzaDays;
        
        azaDaysItemsControl.ItemsSource = _viewModel.AzaDays;
        
        hasAzaDaysCheckBox.IsCheckedChanged += (s, e) =>
        {
            var isChecked = hasAzaDaysCheckBox.IsChecked == true;
            // Reflect in UI
            azaDaysPanel.IsVisible = isChecked;
            // Update ViewModel so calculation respects the checkbox
            _viewModel.HasAzaDays = isChecked;
            // Ensure UI is refreshed after recalculation
            UpdateUI();
        };

        // Subscribe to ViewModel property changes and refresh UI for key properties
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(_viewModel.HasErrors) ||
                e.PropertyName == nameof(_viewModel.ErrorMessage) ||
                e.PropertyName == nameof(_viewModel.IsBusy) ||
                e.PropertyName == nameof(_viewModel.RequestedDays) ||
                e.PropertyName == nameof(_viewModel.RemainingDays) ||
                e.PropertyName == nameof(_viewModel.ApprovedDays) ||
                e.PropertyName == nameof(_viewModel.AnnualEntitlement))
            {
                UpdateUI();
            }
        };
    }

    private void UpdateDateDisplay()
    {
        var startDateButton = this.FindControl<Button>("StartDateButton");
        var endDateButton = this.FindControl<Button>("EndDateButton");
        
        if (startDateButton != null)
        {
            startDateButton.Content = _viewModel.StartDate.ToString("dd.MM.yyyy");
        }
        
        if (endDateButton != null)
        {
            endDateButton.Content = _viewModel.EndDate.ToString("dd.MM.yyyy");
        }
    }

    private async void StartDateButton_Click(object? sender, RoutedEventArgs e)
    {
        var approvedVacationDates = await _viewModel.GetApprovedVacationDatesAsync();

        var dialog = new DateRangePickerDialog(
            _viewModel.StartDate, 
            _viewModel.EndDate, 
            _viewModel.PublicHolidayProvider, 
            _viewModel.SchoolHolidayProvider, 
            _viewModel.State,
            _viewModel.StudentActive,
            _viewModel.VocationalSchoolSettings,
            approvedVacationDates
        );
        var result = await dialog.ShowDialog<(DateOnly, DateOnly)?>(this);
        
        if (result.HasValue)
        {
            _viewModel.StartDate = result.Value.Item1;
            _viewModel.EndDate = result.Value.Item2;
            UpdateDateDisplay();
            UpdateUI();
        }
    }

    private async void EndDateButton_Click(object? sender, RoutedEventArgs e)
    {
        var approvedVacationDates = await _viewModel.GetApprovedVacationDatesAsync();

        var dialog = new DateRangePickerDialog(
            _viewModel.StartDate, 
            _viewModel.EndDate, 
            _viewModel.PublicHolidayProvider, 
            _viewModel.SchoolHolidayProvider, 
            _viewModel.State,
            _viewModel.StudentActive,
            _viewModel.VocationalSchoolSettings,
            approvedVacationDates
        );
        var result = await dialog.ShowDialog<(DateOnly, DateOnly)?>(this);
        
        if (result.HasValue)
        {
            _viewModel.StartDate = result.Value.Item1;
            _viewModel.EndDate = result.Value.Item2;
            UpdateDateDisplay();
            UpdateUI();
        }
    }

    private void UpdateUI()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var userInfoTextBlock = this.FindControl<TextBlock>("UserInfoTextBlock")!;
            var errorBorder = this.FindControl<Border>("ErrorBorder")!;
            var errorTextBlock = this.FindControl<TextBlock>("ErrorTextBlock")!;
            var entitlementTextBlock = this.FindControl<TextBlock>("EntitlementTextBlock")!;
            var requestedDaysTextBlock = this.FindControl<TextBlock>("RequestedDaysTextBlock")!;
            var approvedDaysTextBlock = this.FindControl<TextBlock>("ApprovedDaysTextBlock")!;
            var remainingDaysTextBlock = this.FindControl<TextBlock>("RemainingDaysTextBlock")!;
            var exportButton = this.FindControl<Button>("ExportButton")!;
            
            // Update date display
            UpdateDateDisplay();
            
            // Update user info
            if (!string.IsNullOrEmpty(_viewModel.SettingsName))
            {
                userInfoTextBlock.Text = $"{_viewModel.SettingsName} - {_viewModel.SettingsAbteilung}";
            }

            // Update error display
            errorBorder.IsVisible = _viewModel.HasErrors;
            errorTextBlock.Text = _viewModel.ErrorMessage;

            // Update summary
            // Display annual entitlement from settings (fixed value, does not change)
            entitlementTextBlock.Text = $"{_viewModel.AnnualEntitlement:F1} Tage";
            requestedDaysTextBlock.Text = $"{_viewModel.RequestedDays:F1} Tage";
            approvedDaysTextBlock.Text = $"{_viewModel.ApprovedDays:F1} Tage";
            remainingDaysTextBlock.Text = $"{_viewModel.RemainingDays:F1} Tage";

            // Enable/disable export button based on errors
            exportButton.IsEnabled = !_viewModel.HasErrors && !_viewModel.IsBusy;

            var exportStatus = this.FindControl<TextBlock>("ExportStatusTextBlock")!;
            exportStatus.Text = _viewModel.IsBusy ? "Exportiere…" : string.Empty;
        });
    }

    // Calculate_Click removed: Calculation is triggered live from ViewModel property setters.

    private void AddAzaDate_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var picker = this.FindControl<DatePicker>("AzaNewDatePicker");
            if (picker == null || !picker.SelectedDate.HasValue)
            {
                // No date selected
                _ = ShowMessageBox("Hinweis", "Bitte wählen Sie ein Datum, bevor Sie es hinzufügen.");
                return;
            }

            var dto = picker.SelectedDate.Value;
            var dateOnly = DateOnly.FromDateTime(dto.LocalDateTime);
            _viewModel.AddAzaDate(dateOnly);

            // If VM reported an error (duplicate / out-of-range), show it to the user
            if (_viewModel.HasErrors && !string.IsNullOrEmpty(_viewModel.ErrorMessage))
            {
                _ = ShowMessageBox("AZA hinzufügen fehlgeschlagen", _viewModel.ErrorMessage);
            }
            else
            {
                // Clear picker for next entry
                picker.SelectedDate = null;
            }
        }
        catch (Exception ex)
        {
            _ = ShowMessageBox("Fehler", ex.Message);
        }
    }

    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.HasErrors)
        {
            // Focus the date button to guide the user
            var startDateButton = this.FindControl<Button>("StartDateButton");
            startDateButton?.Focus();

            await ShowMessageBox("Fehler", "PDF-Export nicht möglich, da Fehler vorliegen. Bitte beheben Sie die Fehler und versuchen Sie es erneut.");
            return;
        }

        var pdfPath = await _viewModel.ExportPdfAsync();
        
        // Check if export was successful by examining ViewModel's ErrorMessage and HasErrors status
        if (_viewModel.HasErrors)
        {
            // Export failed - display the detailed error message from ViewModel
            await ShowMessageBox("PDF-Export fehlgeschlagen", _viewModel.ErrorMessage);
            return;
        }

        if (pdfPath != null && File.Exists(pdfPath))
        {
            await ShowExportResultDialog(pdfPath);

            // Refresh approved days and UI after export
            await _viewModel.InitializeAsync();
            UpdateUI();
        }
        else
        {
            // Fallback: should not reach here if error handling is correct, but handle gracefully
            await ShowMessageBox("Fehler", "PDF-Export fehlgeschlagen. Bitte versuchen Sie es erneut.");
        }
    }

    private async System.Threading.Tasks.Task ShowExportResultDialog(string pdfPath)
    {
        var dlg = new Window
        {
            Title = "Export abgeschlossen",
            Width = 520,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var panel = new StackPanel { Margin = new Avalonia.Thickness(18), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = $"PDF erfolgreich exportiert:\n{pdfPath}", TextWrapping = Avalonia.Media.TextWrapping.Wrap });

        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var openPdf = new Button { Content = "PDF öffnen", Width = 120 };
        var openFolder = new Button { Content = "Ordner öffnen", Width = 120 };
        var ok = new Button { Content = "OK", Width = 100 };

        openPdf.Click += (_, _) =>
        {
            try
            {
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
                {
                    System.Diagnostics.Process.Start("open", pdfPath);
                }
                else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pdfPath) { UseShellExecute = true });
                }
                else
                {
                    System.Diagnostics.Process.Start("xdg-open", pdfPath);
                }
            }
            catch (Exception ex)
            {
                _ = ShowMessageBox("Fehler", $"Konnte PDF nicht öffnen: {ex.Message}");
            }
        };

        openFolder.Click += (_, _) =>
        {
            try
            {
                var dir = Path.GetDirectoryName(pdfPath) ?? ".";
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
                {
                    System.Diagnostics.Process.Start("open", dir);
                }
                else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
                }
                else
                {
                    System.Diagnostics.Process.Start("xdg-open", dir);
                }
            }
            catch (Exception ex)
            {
                _ = ShowMessageBox("Fehler", $"Konnte Ordner nicht öffnen: {ex.Message}");
            }
        };

        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
        ok.Click += (_, _) => { tcs.TrySetResult(true); dlg.Close(); };

        buttons.Children.Add(openPdf);
        buttons.Children.Add(openFolder);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        dlg.Content = panel;
        await dlg.ShowDialog(this);
        await tcs.Task;
    }

    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow();
        await settingsWindow.ShowDialog(this);
        
        // Reload settings after window closes
        await _viewModel.ReloadSettingsAsync();
        UpdateUI();
    }

    private async System.Threading.Tasks.Task ShowMessageBox(string title, string message)
    {
        var messageBox = new Window
        {
            Title = title,
            Width = 450,
            Height = 200,
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
            FontSize = 14
        });

        var button = new Button
        {
            Content = "OK",
            Width = 100,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        button.Click += (s, e) => messageBox.Close();
        panel.Children.Add(button);

        messageBox.Content = panel;
        await messageBox.ShowDialog(this);
    }

    private async System.Threading.Tasks.Task<bool> ShowConfirmDialog(string title, string message)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 480,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var panel = new StackPanel { Margin = new Avalonia.Thickness(18), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });

        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var yes = new Button { Content = "Ja", Width = 90 };
        var no = new Button { Content = "Nein", Width = 90 };

        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
        yes.Click += (_, _) => { tcs.TrySetResult(true); dlg.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); dlg.Close(); };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);

        panel.Children.Add(buttons);
        dlg.Content = panel;

        await dlg.ShowDialog(this);
        return await tcs.Task;
    }

    private async System.Threading.Tasks.Task<string?> ShowInputDialog(string title, string prompt)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 520,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var panel = new StackPanel { Margin = new Avalonia.Thickness(18), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = Avalonia.Media.TextWrapping.Wrap });

        var input = new TextBox { Width = 460 };
        panel.Children.Add(input);

        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Abbrechen", Width = 100 };
        var ok = new Button { Content = "OK", Width = 100 };

        var tcs = new System.Threading.Tasks.TaskCompletionSource<string?>();
        ok.Click += (_, _) => { tcs.TrySetResult(input.Text); dlg.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dlg.Close(); };

        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        dlg.Content = panel;
        await dlg.ShowDialog(this);
        return await tcs.Task;
    }

    private void ShowDayBreakdown_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Breakdown.Count == 0)
        {
            _ = ShowMessageBox("Hinweis", "Keine Tagesaufschlüsselung verfügbar. Bitte geben Sie zuerst einen Urlaubszeitraum ein.");
            return;
        }

        var window = new DayBreakdownWindow(_viewModel.Breakdown, (int)_viewModel.RequestedDays);
        window.ShowDialog(this);
    }

    private void Exit_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
