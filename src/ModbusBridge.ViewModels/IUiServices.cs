namespace ModbusBridge.ViewModels;

/// <summary>How a view model asks a question it must not answer itself.</summary>
public interface IDialogService
{
    /// <summary>Tells the user something. Returns when they have seen it.</summary>
    void Inform(string message, string title, bool warning = false);

    /// <summary>Asks a yes/no question. Returns true only for an explicit yes.</summary>
    bool Confirm(string message, string title);
}

/// <summary>
/// Marshals work onto whatever thread the UI requires.
///
/// WPF and Avalonia both have a dispatcher and neither exposes it the same way, so the view models
/// take this instead of reaching for <c>Application.Current.Dispatcher</c>.
/// </summary>
public interface IUiDispatcher
{
    void Post(Action action);
    bool IsOnUiThread { get; }
}

/// <summary>A periodic callback on the UI thread. Each UI supplies its own timer.</summary>
public interface IUiTimer : IDisposable
{
    TimeSpan Interval { get; set; }
    void Start();
    void Stop();
}
