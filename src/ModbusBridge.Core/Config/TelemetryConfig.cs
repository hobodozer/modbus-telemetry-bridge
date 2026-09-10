using System.Collections.ObjectModel;

namespace ModbusBridge.Core.Config;

/// <summary>One SimHub property mapped onto a bridge tag.</summary>
public sealed class TelemetrySubscription
{
    public bool Enabled { get; set; } = true;

    /// <summary>SimHub property name, e.g. "SpeedKmh" or "DataCorePlugin.GameData.Gear".</summary>
    public string Property { get; set; } = "";

    /// <summary>
    /// Tag to publish into. Empty derives one from the property name, with the configured prefix -
    /// so subscribing to a property is normally a one-field operation.
    /// </summary>
    public string Tag { get; set; } = "";

    /// <summary>Optional linear conversion applied before the value reaches the tag.</summary>
    public ScalingConfig? Scale { get; set; }

    public string? Units { get; set; }
    public string? Description { get; set; }

    /// <summary>The tag this subscription actually writes.</summary>
    public string ResolveTag(string prefix) =>
        string.IsNullOrWhiteSpace(Tag) ? prefix + SanitiseName(Property) : Tag;

    /// <summary>SimHub property names contain dots and spaces; keep dots, drop the rest.</summary>
    public static string SanitiseName(string property)
    {
        if (string.IsNullOrWhiteSpace(property)) return "unnamed";
        var cleaned = new string(property
            .Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' ? c : '_')
            .ToArray());
        return cleaned.Trim('_', '.');
    }
}

/// <summary>A bridge tag exposed back into SimHub as a property.</summary>
public sealed class TelemetryFeedback
{
    public bool Enabled { get; set; } = true;

    /// <summary>Bridge tag to send, e.g. a PLC contact.</summary>
    public string Tag { get; set; } = "";

    /// <summary>
    /// Name it appears under inside SimHub, below the plugin's own root
    /// (so "estop" surfaces as ModbusBridge.estop and can be bound like any other property).
    /// </summary>
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public string ResolveName() => string.IsNullOrWhiteSpace(Name)
        ? TelemetrySubscription.SanitiseName(Tag)
        : Name;
}

/// <summary>Telemetry ingest from the SimHub plugin.</summary>
public sealed class TelemetryConfig
{
    public bool Enabled { get; set; }

    /// <summary>UDP port the bridge listens on. The plugin must be pointed at the same one.</summary>
    public int ListenPort { get; set; } = 15600;

    /// <summary>Loopback by default - the plugin runs on this machine.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>Prefix for auto-derived tag names.</summary>
    public string TagPrefix { get; set; } = "sim.";

    /// <summary>Rate the plugin is asked to stream at. SimHub's own data rate is the real ceiling.</summary>
    public int UpdateIntervalMs { get; set; } = 16;

    /// <summary>
    /// Mark telemetry tags bad if no frame arrives within this long, so an HMI can tell
    /// "game closed" from "value happens to be zero". 0 disables.
    /// </summary>
    public int TimeoutMs { get; set; } = 2000;

    /// <summary>How often bridge tag values are pushed back to the plugin. 0 disables feedback.</summary>
    public int FeedbackIntervalMs { get; set; } = 50;

    /// <summary>Tag set true while the plugin is connected and sending.</summary>
    public string ConnectedTag { get; set; } = "bridge.simhubConnected";

    /// <summary>Tag holding the running game's name, as SimHub reports it.</summary>
    public string GameNameTag { get; set; } = "bridge.simhubGame";

    public ObservableCollection<TelemetrySubscription> Subscriptions { get; set; } = new();

    public ObservableCollection<TelemetryFeedback> Feedback { get; set; } = new();
}

/// <summary>
/// Host machine statistics. Independent of any game - an HMI showing CPU and RAM should work
/// with nothing running.
/// </summary>
public sealed class PcStatsConfig
{
    public bool Enabled { get; set; }

    /// <summary>Sample period. CPU and network are rates, so this also sets their averaging window.</summary>
    public int IntervalMs { get; set; } = 1000;

    public string TagPrefix { get; set; } = "pc.";
}

/// <summary>
/// A tag computed from other tags. Exists so values that are a function of existing data are
/// configuration rather than code - fuel percent from level and capacity, a status bitmask, a
/// unit conversion the source does not offer.
/// </summary>
public sealed class DerivedTagConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Tag to publish into. Created if it does not exist.</summary>
    public string Tag { get; set; } = "";

    /// <summary>
    /// Arithmetic over other tags, e.g. <c>fs.fuelLevel / fs.fuelCapacity * 100</c>.
    /// Supports + - * / %, comparisons, &amp;&amp; and ||, and
    /// abs/min/max/clamp/round/floor/ceil/sqrt/if.
    /// </summary>
    public string Expression { get; set; } = "";

    /// <summary>
    /// Require every referenced tag to be Good before publishing. On by default: a value derived
    /// from a stale reading looks exactly like a real one on a gauge.
    /// </summary>
    public bool RequireGoodInputs { get; set; } = true;

    public string? Description { get; set; }
    public string? Units { get; set; }
}

/// <summary>Captures selected tags to CSV so a session can be replayed later.</summary>
public sealed class RecordingConfig
{
    public bool Enabled { get; set; }

    /// <summary>Where files are written. Relative paths are relative to the data directory.</summary>
    public string Directory { get; set; } = "recordings";

    /// <summary>Fixed name, or empty to timestamp each run.</summary>
    public string FileName { get; set; } = "";

    /// <summary>Tag name patterns; '*' matches anything, e.g. "sim.*".</summary>
    public ObservableCollection<string> Tags { get; set; } = new() { "*" };

    public int IntervalMs { get; set; } = 100;

    /// <summary>Skip a row when nothing moved, which keeps an idle capture small.</summary>
    public bool OnChangeOnly { get; set; }

    /// <summary>Stop after this many rows. 0 records until the engine stops.</summary>
    public int MaxRows { get; set; }
}

/// <summary>Publishes a recorded CSV back onto the tag bus, as a source in its own right.</summary>
public sealed class ReplayConfig
{
    public bool Enabled { get; set; }

    public string Path { get; set; } = "";

    /// <summary>Playback rate. 2 is twice as fast; 0.5 is half speed.</summary>
    public double Speed { get; set; } = 1.0;

    public bool Loop { get; set; }

    /// <summary>Prepended to every column name, to replay a capture into a different namespace.</summary>
    public string TagPrefix { get; set; } = "";
}
