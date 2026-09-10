namespace ModbusBridge.ViewModels;

/// <summary>
/// What a status indicator means, rather than what colour it is.
///
/// The view models used to expose <c>Brush</c> directly, which pinned them to WPF and would have
/// pinned them to Avalonia just as hard. A view model knows a device is faulted; it has no
/// business knowing that faulted is IndianRed. Each UI maps these to its own resources.
/// </summary>
public enum HealthState
{
    /// <summary>No information yet, or the thing is switched off. Grey.</summary>
    Unknown,

    /// <summary>Working. Green.</summary>
    Healthy,

    /// <summary>Working, but not well - degraded timing, stale data, controls released. Amber.</summary>
    Warning,

    /// <summary>Not working. Red.</summary>
    Fault,

    /// <summary>Transitional - connecting, reconnecting. Blue.</summary>
    Busy,

    /// <summary>Ordinary content with no health meaning, such as an informational log line.</summary>
    Neutral
}
