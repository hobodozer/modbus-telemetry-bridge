using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace ModbusBridge.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        return true;
    }
}

/// <summary>Minimal ICommand so the XAML can bind buttons without pulling in an MVVM framework.</summary>
/// <summary>
/// How a command's CanExecute gets re-evaluated. WPF does this globally through
/// <c>CommandManager.RequerySuggested</c>, which fires on UI activity and keeps buttons enabled or
/// disabled without anyone asking. Avalonia has no equivalent - a command raises its own event.
///
/// So the shared commands raise their own event, and a host that HAS a global requery wires it in
/// here. WPF does; Avalonia leaves it null and gets explicit behaviour. Without this the WPF app
/// would silently lose automatic button enabling, which is the kind of regression that is noticed
/// weeks later by a button that will not click.
/// </summary>
public static class CommandRequery
{
    public static Action<EventHandler>? Subscribe;
    public static Action<EventHandler>? Unsubscribe;
    public static Action? InvalidateAll;
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    private EventHandler? _canExecuteChanged;

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            _canExecuteChanged += value;
            if (value is not null) CommandRequery.Subscribe?.Invoke(value);
        }
        remove
        {
            _canExecuteChanged -= value;
            if (value is not null) CommandRequery.Unsubscribe?.Invoke(value);
        }
    }

    /// <summary>Re-evaluates this command. Only needed where there is no global requery.</summary>
    public void RaiseCanExecuteChanged() => _canExecuteChanged?.Invoke(this, EventArgs.Empty);

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);
}

/// <summary>Async variant that keeps the button disabled while the operation is in flight.</summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = _ => execute();
        _canExecute = canExecute is null ? null : _ => canExecute();
    }

    private EventHandler? _canExecuteChanged;

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            _canExecuteChanged += value;
            if (value is not null) CommandRequery.Subscribe?.Invoke(value);
        }
        remove
        {
            _canExecuteChanged -= value;
            if (value is not null) CommandRequery.Unsubscribe?.Invoke(value);
        }
    }

    public void RaiseCanExecuteChanged() => _canExecuteChanged?.Invoke(this, EventArgs.Empty);

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        _running = true;
        RaiseCanExecuteChanged();
        CommandRequery.InvalidateAll?.Invoke();
        try
        {
            await _execute(parameter);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
            CommandRequery.InvalidateAll?.Invoke();
        }
    }
}
