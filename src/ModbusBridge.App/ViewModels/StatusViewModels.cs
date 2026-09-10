using ModbusBridge.Core.Data;
using ModbusBridge.Core.Diagnostics;
using ModbusBridge.Core.Engine;
using ModbusBridge.Core.Inputs;
using ModbusBridge.Core.Modbus;
using ModbusBridge.Core.Outputs.VJoy;
using ModbusBridge.Core.Tags;

namespace ModbusBridge.App.ViewModels;

/// <summary>Live status of one PLC connection, refreshed on the UI timer.</summary>
public sealed class DeviceStatusViewModel : ObservableObject
{
    private readonly DeviceRunner _runner;

    public DeviceStatusViewModel(DeviceRunner runner)
    {
        _runner = runner;
        ResetLatencyCommand = new RelayCommand(() =>
        {
            _runner.Statistics.ResetLatency();
            Refresh();
        });
    }

    public DeviceRunner Runner => _runner;
    public string Name => _runner.Name;
    public string Endpoint => _runner.Endpoint;
    public int MappedPointCount => _runner.MappedPointCount;
    public RelayCommand ResetLatencyCommand { get; }

    public DeviceState State => _runner.State;

    public string StateText => _runner.State switch
    {
        DeviceState.Online => _runner.LinkHealthy ? "Online" : "Online (watchdog stale)",
        DeviceState.Connecting => "Connecting...",
        DeviceState.Faulted => "Faulted",
        DeviceState.Disabled => "Disabled",
        _ => "Offline"
    };

    public Brush StateBrush => _runner.State switch
    {
        DeviceState.Online => _runner.LinkHealthy ? Brushes.MediumSeaGreen : Brushes.Goldenrod,
        DeviceState.Connecting => Brushes.SteelBlue,
        DeviceState.Faulted => Brushes.IndianRed,
        DeviceState.Disabled => Brushes.Gray,
        _ => Brushes.IndianRed
    };

    // ---- The latency readout ----
    public string LastLatency => Format(_runner.Statistics.LastLatencyMs);
    public string AverageLatency => Format(_runner.Statistics.AverageLatencyMs);
    public string MinLatency => Format(_runner.Statistics.MinLatencyMs);
    public string MaxLatency => Format(_runner.Statistics.MaxLatencyMs);
    public string Jitter => Format(_runner.Statistics.JitterMs);
    public string CycleTime => Format(_runner.Statistics.CycleTimeMs);
    public string MaxCycleTime => Format(_runner.Statistics.MaxCycleTimeMs);
    public string PollRate => $"{_runner.Statistics.PollsPerSecond:0.0} /s";

    public long Polls => _runner.Statistics.Polls;
    public long Writes => _runner.Statistics.Writes;
    public long Errors => _runner.Statistics.Errors;
    public long Timeouts => _runner.Statistics.Timeouts;
    public string LastError => _runner.Statistics.LastErrorMessage ?? "-";

    /// <summary>Red once the achieved cycle drifts well past what was configured.</summary>
    public Brush CycleBrush
    {
        get
        {
            var timings = _runner.ReadGroupTimings();
            if (timings.Count == 0) return Brushes.Gray;
            var worst = timings.Max(t => t.ActualCycleMs - t.ConfiguredIntervalMs);
            return worst switch
            {
                < 2 => Brushes.MediumSeaGreen,
                < 10 => Brushes.Goldenrod,
                _ => Brushes.IndianRed
            };
        }
    }

    public IReadOnlyList<ReadGroupTiming> GroupTimings => _runner.ReadGroupTimings();

    private static string Format(double ms) =>
        double.IsNaN(ms) ? "-" : ms >= 100 ? $"{ms:0} ms" : ms >= 10 ? $"{ms:0.0} ms" : $"{ms:0.00} ms";

    public void Refresh()
    {
        Raise(nameof(State));
        Raise(nameof(StateText));
        Raise(nameof(StateBrush));
        Raise(nameof(LastLatency));
        Raise(nameof(AverageLatency));
        Raise(nameof(MinLatency));
        Raise(nameof(MaxLatency));
        Raise(nameof(Jitter));
        Raise(nameof(CycleTime));
        Raise(nameof(MaxCycleTime));
        Raise(nameof(CycleBrush));
        Raise(nameof(PollRate));
        Raise(nameof(Polls));
        Raise(nameof(Writes));
        Raise(nameof(Errors));
        Raise(nameof(Timeouts));
        Raise(nameof(LastError));
        Raise(nameof(GroupTimings));
    }
}

/// <summary>Live status of one listener the HMIs connect to.</summary>
public sealed class ServerStatusViewModel : ObservableObject
{
    private readonly ServerInstance _instance;

    public ServerStatusViewModel(ServerInstance instance)
    {
        _instance = instance;
    }

    public string Name => _instance.Config.Name;
    public string Endpoint => $"{_instance.Config.BindAddress}:{_instance.Config.Port}";
    public int PointCount => _instance.Store.PointCount;
    public int MapCount => _instance.Config.Maps.Count(m => m.Enabled);
    public string UnitIds => string.Join(", ", _instance.Config.Maps.Where(m => m.Enabled).Select(m => m.UnitId));

    public bool IsRunning => _instance.Server.IsRunning;
    public string StateText => _instance.StartupError is { } error ? $"Failed: {error}"
                             : _instance.Server.IsRunning ? "Listening" : "Stopped";

    public Brush StateBrush => _instance.StartupError is not null ? Brushes.IndianRed
                             : _instance.Server.IsRunning ? Brushes.MediumSeaGreen : Brushes.Gray;

    public int ClientCount => _instance.Server.Sessions.Count;

    public IReadOnlyList<ClientSessionViewModel> Clients =>
        _instance.Server.Sessions
                 .OrderBy(s => s.ConnectedAtLocal)
                 .Select(s => new ClientSessionViewModel(s))
                 .ToList();

    public void Refresh()
    {
        Raise(nameof(StateText));
        Raise(nameof(StateBrush));
        Raise(nameof(IsRunning));
        Raise(nameof(ClientCount));
        Raise(nameof(Clients));
    }
}

public sealed class ClientSessionViewModel
{
    public ClientSessionViewModel(ModbusClientSession session)
    {
        RemoteEndPoint = session.RemoteEndPoint;
        ConnectedAt = session.ConnectedAtLocal.ToString("HH:mm:ss");
        Requests = session.Requests;
        Errors = session.Errors;
        LastUnitId = session.LastUnitId;
        LastFunction = $"0x{session.LastFunctionCode:X2}";
        var idle = (DateTime.Now - session.LastRequestLocal).TotalMilliseconds;
        IdleMs = idle < 1000 ? $"{idle:0} ms" : $"{idle / 1000:0.0} s";
    }

    public string RemoteEndPoint { get; }
    public string ConnectedAt { get; }
    public long Requests { get; }
    public long Errors { get; }
    public byte LastUnitId { get; }
    public string LastFunction { get; }
    public string IdleMs { get; }
}

/// <summary>One row in the live tag monitor.</summary>
public sealed class TagRowViewModel : ObservableObject
{
    private long _lastVersion = -1;

    public TagRowViewModel(TagEntry entry)
    {
        Entry = entry;
    }

    public TagEntry Entry { get; }
    public string Name => Entry.Name;
    public string? Units => Entry.Units;
    public string? Description => Entry.Description;
    public string DataType => Entry.DataType.ToString();
    public string Source => Entry.LastWriterId ?? "-";
    public bool IsForced => Entry.IsForced;

    public string Value
    {
        get
        {
            var value = Entry.Value;
            if (value.Quality == TagQuality.Never) return "-";
            if (value.Text is not null) return value.Text;
            return Entry.DataType switch
            {
                PointDataType.Bool => value.Number != 0 ? "true" : "false",
                PointDataType.Float32 or PointDataType.Float64 => value.Number.ToString("0.###"),
                _ => value.Number.ToString("0.###")
            };
        }
    }

    public string Quality => Entry.Value.Quality.ToString();

    public Brush QualityBrush => Entry.Value.Quality switch
    {
        TagQuality.Good => Brushes.MediumSeaGreen,
        TagQuality.Stale => Brushes.Goldenrod,
        TagQuality.Bad => Brushes.IndianRed,
        _ => Brushes.Gray
    };

    /// <summary>How long since this tag last changed - the end of the latency chain the user sees.</summary>
    public string Age
    {
        get
        {
            var value = Entry.Value;
            if (value.Quality == TagQuality.Never) return "-";
            var ms = (DateTime.UtcNow - value.TimestampUtc).TotalMilliseconds;
            return ms < 1000 ? $"{ms:0} ms" : ms < 60000 ? $"{ms / 1000:0.0} s" : $"{ms / 60000:0} m";
        }
    }

    /// <summary>Refreshes only when something actually moved, to keep a big grid cheap.</summary>
    public void Refresh()
    {
        var version = Entry.Version;
        var changed = version != _lastVersion;
        _lastVersion = version;

        if (changed)
        {
            Raise(nameof(Value));
            Raise(nameof(Quality));
            Raise(nameof(QualityBrush));
            Raise(nameof(Source));
            Raise(nameof(IsForced));
        }

        // Age moves with the clock even when the value does not.
        Raise(nameof(Age));
    }
}

/// <summary>Live status of one vJoy feeder.</summary>
public sealed class VJoyStatusViewModel : ObservableObject
{
    private readonly VJoyFeeder _feeder;

    public VJoyStatusViewModel(VJoyFeeder feeder)
    {
        _feeder = feeder;
        ResetCommand = new RelayCommand(() => { _feeder.Statistics.Reset(); Refresh(); });
    }

    public RelayCommand ResetCommand { get; }

    public string Name => $"vJoy device {_feeder.DeviceId}";

    public string Capabilities => _feeder.Capabilities is { } caps
        ? $"{caps.ButtonCount} buttons, {caps.Axes.Count} axes ({string.Join(" ", caps.Axes.Keys)}), " +
          $"{caps.ContinuousPovCount} POV"
        : "not acquired";

    public bool IsRunning => _feeder.IsRunning;

    public string StateText => _feeder.IsRunning
        ? _feeder.Statistics.Released ? "Running (controls released - tags bad)" : "Running"
        : _feeder.Error ?? "Stopped";

    public Brush StateBrush => !_feeder.IsRunning ? Brushes.IndianRed
                             : _feeder.Statistics.Released ? Brushes.Goldenrod
                             : Brushes.MediumSeaGreen;

    public string LoopInterval => Format(_feeder.Statistics.LoopIntervalMs);
    public string WorstLoop => Format(_feeder.Statistics.MaxLoopIntervalMs);
    public int ButtonsPressed => _feeder.Statistics.ButtonsPressed;
    public long Updates => _feeder.Statistics.Updates;
    public long Failures => _feeder.Statistics.Failures;

    public int MappedButtons => _feeder.Config.Buttons.Count(b => b.Enabled);
    public int MappedAxes => _feeder.Config.Axes.Count(a => a.Enabled);

    public IReadOnlyList<string> Warnings => _feeder.Warnings;
    public bool HasWarnings => _feeder.Warnings.Count > 0;

    private static string Format(double ms) =>
        double.IsNaN(ms) ? "-" : ms >= 10 ? $"{ms:0.0} ms" : $"{ms:0.00} ms";

    public void Refresh()
    {
        Raise(nameof(IsRunning));
        Raise(nameof(StateText));
        Raise(nameof(StateBrush));
        Raise(nameof(LoopInterval));
        Raise(nameof(WorstLoop));
        Raise(nameof(ButtonsPressed));
        Raise(nameof(Updates));
        Raise(nameof(Failures));
        Raise(nameof(Capabilities));
        Raise(nameof(Warnings));
        Raise(nameof(HasWarnings));
    }
}

/// <summary>Live status of the SimHub telemetry link.</summary>
public sealed class TelemetryStatusViewModel : ObservableObject
{
    private readonly TelemetryIngestServer _ingest;

    public TelemetryStatusViewModel(TelemetryIngestServer ingest)
    {
        _ingest = ingest;
        ResetCommand = new RelayCommand(() => { _ingest.Statistics.Reset(); Refresh(); });
    }

    public RelayCommand ResetCommand { get; }

    public string Name => "SimHub telemetry";

    public bool IsConnected => _ingest.IsConnected;

    public string StateText => !_ingest.IsRunning ? _ingest.Error ?? "Stopped"
                             : _ingest.IsConnected ? "Streaming"
                             : _ingest.Statistics.PluginVersion is null
                                 ? "Waiting for the SimHub plugin"
                                 : "Plugin silent";

    public Brush StateBrush => !_ingest.IsRunning ? Brushes.IndianRed
                             : _ingest.IsConnected ? Brushes.MediumSeaGreen
                             : Brushes.Goldenrod;

    public string PluginVersion => _ingest.Statistics.PluginVersion ?? "-";
    public string GameName => string.IsNullOrWhiteSpace(_ingest.Statistics.GameName)
        ? "(no game)" : _ingest.Statistics.GameName!;

    public string Rate => $"{_ingest.Statistics.Rate:0.0} /s";
    public string Interval => Format(_ingest.Statistics.IntervalMs);
    public string WorstInterval => Format(_ingest.Statistics.MaxIntervalMs);
    public string Latency => Format(_ingest.Statistics.LatencyMs);
    public long Frames => _ingest.Statistics.Frames;
    public long Dropped => _ingest.Statistics.Dropped;
    public int CatalogCount => _ingest.Catalog.Count;

    private static string Format(double ms) =>
        double.IsNaN(ms) ? "-" : ms >= 100 ? $"{ms:0} ms" : ms >= 10 ? $"{ms:0.0} ms" : $"{ms:0.00} ms";

    public void Refresh()
    {
        Raise(nameof(IsConnected));
        Raise(nameof(StateText));
        Raise(nameof(StateBrush));
        Raise(nameof(PluginVersion));
        Raise(nameof(GameName));
        Raise(nameof(Rate));
        Raise(nameof(Interval));
        Raise(nameof(WorstInterval));
        Raise(nameof(Latency));
        Raise(nameof(Frames));
        Raise(nameof(Dropped));
        Raise(nameof(CatalogCount));
    }
}

public sealed class LogRowViewModel
{
    public LogRowViewModel(LogEntry entry)
    {
        Time = entry.TimestampLocal.ToString("HH:mm:ss.fff");
        Level = entry.Level.ToString();
        Source = entry.Source;
        Message = entry.Message;
        Brush = entry.Level switch
        {
            LogLevel.Error => Brushes.IndianRed,
            LogLevel.Warn => Brushes.Goldenrod,
            LogLevel.Debug or LogLevel.Trace => Brushes.Gray,
            _ => Brushes.Gainsboro
        };
        RawLevel = entry.Level;
    }

    public string Time { get; }
    public string Level { get; }
    public string Source { get; }
    public string Message { get; }
    public Brush Brush { get; }
    public LogLevel RawLevel { get; }
}
