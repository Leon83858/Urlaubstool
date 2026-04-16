using System;
using System.Windows.Input;

namespace Urlaubstool.App.ViewModels;

/// <summary>
/// Represents a single AZA day selection in the UI
/// </summary>
public class AzaDayItem : ViewModelBase
{
    private DateTimeOffset? _date;
    
    public DateTimeOffset? Date
    {
        get => _date;
        set => SetProperty(ref _date, value);
    }

    public ICommand RemoveCommand { get; }

    public AzaDayItem(Action<AzaDayItem> onRemove)
    {
        RemoveCommand = new RelayCommand(() => onRemove(this));
        _date = new DateTimeOffset(DateTime.Today);
    }
}

/// <summary>
/// Simple relay command implementation for synchronous operations
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Async relay command implementation for asynchronous operations.
/// Prevents multiple concurrent executions to avoid race conditions.
/// Disables the command while executing to prevent double-clicks.
/// </summary>
public class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _executeAsync;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
        _isExecuting = false;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (_isExecuting || !CanExecute(parameter))
            return;

        try
        {
            _isExecuting = true;
            RaiseCanExecuteChanged();
            
            await _executeAsync();
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
