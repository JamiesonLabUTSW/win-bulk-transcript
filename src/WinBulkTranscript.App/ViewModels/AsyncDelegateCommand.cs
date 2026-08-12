using System.Windows.Input;

namespace WinBulkTranscript.App.ViewModels;

/// <summary>Minimal asynchronous command that blocks re-entry and forwards failures to its owner.</summary>
public sealed class AsyncDelegateCommand : ObservableObject, ICommand
{
    private readonly Func<Task> _executeAsync;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _exceptionHandler;
    private bool _isExecuting;

    /// <summary>Initializes the command.</summary>
    public AsyncDelegateCommand(Func<Task> executeAsync, Func<bool>? canExecute = null, Action<Exception>? exceptionHandler = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
        _exceptionHandler = exceptionHandler;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <summary>Gets whether a command invocation is currently running.</summary>
    public bool IsExecuting
    {
        get => _isExecuting;
        private set => SetProperty(ref _isExecuting, value);
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !IsExecuting && (_canExecute?.Invoke() ?? true);

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _ = InvokeAsync();
        }
    }

    /// <summary>Re-evaluates command availability.</summary>
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private async Task InvokeAsync()
    {
        IsExecuting = true;
        NotifyCanExecuteChanged();
        try
        {
            await _executeAsync();
        }
        catch (Exception exception)
        {
            _exceptionHandler?.Invoke(exception);
        }
        finally
        {
            IsExecuting = false;
            NotifyCanExecuteChanged();
        }
    }
}
