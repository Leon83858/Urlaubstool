using System;
using System.Linq;
using System.Windows.Input;
using Urlaubstool.Domain;

namespace Urlaubstool.App.ViewModels;

/// <summary>
/// ViewModel for displaying a vacation request in the main window history grid.
/// Supports async approve and delete operations with async command pattern.
/// </summary>
public class MainHistoryEntryViewModel : ViewModelBase
{
    private readonly HistoryEntry _entry;
    private readonly Func<Guid, Task> _onApprove;
    private readonly Func<Guid, Task> _onDelete;
    private readonly Func<Guid, Task> _onArchive;
    private readonly Func<Guid, string, Task>? _onReject;

    /// <summary>
    /// Creates a history entry view model.
    /// </summary>
    /// <param name="entry">Underlying history entry.</param>
    /// <param name="onApprove">Callback to approve the request.</param>
    /// <param name="onDelete">Callback to delete the request.</param>
    /// <param name="onArchive">Callback to archive the request.</param>
    /// <param name="onReject">Callback to reject the request (provides reason).</param>
    public MainHistoryEntryViewModel(HistoryEntry entry, Func<Guid, Task> onApprove, Func<Guid, Task> onDelete, Func<Guid, Task> onArchive, Func<Guid, string, Task>? onReject = null)
    {
        _entry = entry;
        _onApprove = onApprove;
        _onDelete = onDelete;
        _onArchive = onArchive;
        _onReject = onReject;

        ApproveCommand = new AsyncRelayCommand(() => _onApprove(_entry.RequestId));
        DeleteCommand = new AsyncRelayCommand(async () =>
        {
            // Respect optional confirmation callback provided by UI
            if (ConfirmDeleteAsync != null)
            {
                var ok = await ConfirmDeleteAsync();
                if (!ok) return;
            }

            await _onDelete(_entry.RequestId);
        });

        RejectCommand = new AsyncRelayCommand(async () =>
        {
            if (_onReject == null)
            {
                return;
            }

            // Ask the UI for a rejection reason if a callback is provided
            string? reason = null;
            if (RequestRejectionReasonAsync != null)
            {
                reason = await RequestRejectionReasonAsync();
            }

            // If user cancelled or provided empty reason, abort
            if (string.IsNullOrWhiteSpace(reason)) return;

            await _onReject(_entry.RequestId, reason);
        });

        ArchiveCommand = new AsyncRelayCommand(() => _onArchive(_entry.RequestId));
        OpenPdfCommand = new AsyncRelayCommand(OpenPdfAsync);
    }

    /// <summary>
    /// Optional callback the UI can set to request a confirmation from the user before deletion.
    /// Should return true when the delete is confirmed.
    /// </summary>
    public Func<Task<bool>>? ConfirmDeleteAsync { get; set; }

    /// <summary>
    /// Optional callback the UI can set to request a rejection reason string from the user.
    /// Return null or empty string to cancel rejection.
    /// </summary>
    public Func<Task<string?>>? RequestRejectionReasonAsync { get; set; }

    public bool HasPdf => !string.IsNullOrEmpty(_entry.PdfPath) && System.IO.File.Exists(_entry.PdfPath);
    public bool CanApprove => _entry.Status == VacationRequestStatus.Exported || _entry.Status == VacationRequestStatus.Draft;
    public bool CanArchive => _entry.Status == VacationRequestStatus.Approved || _entry.Status == VacationRequestStatus.Rejected;
    public ICommand OpenPdfCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ArchiveCommand { get; }
    public ICommand RejectCommand { get; }

    private async Task OpenPdfAsync()
    {
        if (string.IsNullOrEmpty(_entry.PdfPath)) return;
        
        try 
        {
             if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
             {
                 System.Diagnostics.Process.Start("open", _entry.PdfPath);
             }
             else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
             {
                 System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_entry.PdfPath) { UseShellExecute = true });
             }
             else
             {
                 System.Diagnostics.Process.Start("xdg-open", _entry.PdfPath);
             }
             await Task.CompletedTask;
        }
        catch (Exception) { /* Handle error? */ }
    }

    public string StartDateString => _entry.StartDate.ToString("dd.MM.yyyy");
    public string EndDateString => _entry.EndDate.ToString("dd.MM.yyyy");
    public bool StartHalfDay => _entry.StartHalfDay;
    public bool EndHalfDay => _entry.EndHalfDay;
    
    public string DaysString => $"{_entry.CalculatedDays:0.##} Tage";
    public decimal CalculatedDays => _entry.CalculatedDays;

    public string AzaDatesString
    {
        get
        {
            if (_entry.AzaDates != null && _entry.AzaDates.Count > 0)
            {
                var dates = _entry.AzaDates.OrderBy(d => d).Select(d => d.ToString("dd.MM.yyyy"));
                return string.Join(", ", dates);
            }
            return "-";
        }
    }
    
    public string StatusString => _entry.Status switch
    {
        VacationRequestStatus.Draft => "Entwurf",
        VacationRequestStatus.Exported => "Exportiert",
        VacationRequestStatus.Approved => "Genehmigt",
        VacationRequestStatus.Rejected => "Abgelehnt",
        VacationRequestStatus.Archived => "Archiviert",
        VacationRequestStatus.Deleted => "Gelöscht",
        _ => "Unbekannt"
    };
}
