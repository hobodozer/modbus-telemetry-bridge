using System.Diagnostics;
using System.Windows;
using ModbusBridge.Core.Diagnostics;

namespace ModbusBridge.App.Diagnostics;

/// <summary>
/// Routes WPF's data-binding trace into our own log. Binding mistakes are otherwise silent at
/// runtime (or, worse, surface as a modal exception on one row), so surfacing them makes editor
/// regressions obvious instead of something a user has to trip over.
/// </summary>
public sealed class BindingErrorListener : TraceListener
{
    private readonly List<string> _errors = new();

    private BindingErrorListener() { }

    public static BindingErrorListener? Current { get; private set; }

    public IReadOnlyList<string> Errors
    {
        get { lock (_errors) return _errors.ToArray(); }
    }

    public static void Install(SourceLevels level = SourceLevels.Error)
    {
        if (Current is not null) return;

        Current = new BindingErrorListener();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(Current);
        PresentationTraceSources.DataBindingSource.Switch.Level = level;
    }

    public override void Write(string? message) { }

    public override void WriteLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        lock (_errors)
        {
            // The same broken binding fires once per realised row; report it once.
            if (_errors.Contains(message)) return;
            _errors.Add(message);
        }

        Log.Warn("binding", message);
    }
}
