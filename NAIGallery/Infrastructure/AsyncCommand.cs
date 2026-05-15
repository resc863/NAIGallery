using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace NAIGallery;

internal sealed class AsyncCommand<T> : ICommand
{
    private readonly Func<T?, Task> _execute;
    private readonly Predicate<T?>? _canExecute;
    private bool _isRunning;

    public AsyncCommand(Func<T?, Task> execute, Predicate<T?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return !_isRunning && (_canExecute?.Invoke(ConvertParameter(parameter)) ?? true);
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;

        _isRunning = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute(ConvertParameter(parameter)).ConfigureAwait(false);
        }
        finally
        {
            _isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static T? ConvertParameter(object? parameter)
    {
        if (parameter is null)
            return default;
        if (parameter is T value)
            return value;
        return (T?)System.Convert.ChangeType(parameter, typeof(T));
    }
}
