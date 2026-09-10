using ModbusBridge.Core.Config;
using ModbusBridge.Core.Diagnostics;
using ModbusBridge.Core.Tags;

namespace ModbusBridge.Core.Simulation;

/// <summary>
/// Drives tags with test waveforms so maps and HMI screens can be built and proven before any game
/// or PLC is attached. It publishes under its own writer id, so a real source taking over is
/// visible in the tag monitor.
/// </summary>
public sealed class TelemetrySimulator : IAsyncDisposable
{
    public const string WriterId = "simulator";

    private readonly SimulationConfig _config;
    private readonly TagBus _bus;
    private readonly List<(SimulatedTagConfig Config, TagEntry Tag)> _bound = new();
    private readonly Random _random = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _startTicks;

    public TelemetrySimulator(SimulationConfig config, TagBus bus)
    {
        _config = config;
        _bus = bus;
    }

    public bool IsRunning => _loop is not null;

    /// <summary>Publish rate. 50 Hz is smooth enough for gauges without loading the machine.</summary>
    public int UpdateIntervalMs { get; set; } = 20;

    public void Start()
    {
        if (_loop is not null) return;

        _bound.Clear();
        foreach (var tag in _config.Tags)
        {
            if (string.IsNullOrWhiteSpace(tag.Tag)) continue;
            _bound.Add((tag, _bus.GetOrAdd(tag.Tag)));
        }

        if (_bound.Count == 0)
        {
            Log.Warn(WriterId, "Telemetry simulation is on but no simulated tags are configured.");
            return;
        }

        _startTicks = Environment.TickCount64;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        Log.Info(WriterId, $"Generating test waveforms for {_bound.Count} tag(s).");
    }

    public async Task StopAsync()
    {
        if (_loop is null) return;
        _cts?.Cancel();
        try { await _loop.ConfigureAwait(false); } catch { }
        _loop = null;
        _cts?.Dispose();
        _cts = null;
        _bus.MarkSourceBad(WriterId);
        Log.Info(WriterId, "Stopped.");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(5, UpdateIntervalMs)));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var seconds = (Environment.TickCount64 - _startTicks) / 1000.0;
            foreach (var (config, tag) in _bound)
            {
                // Do not fight a real source that has taken ownership of this tag.
                if (tag.LastWriterId is not null &&
                    !string.Equals(tag.LastWriterId, WriterId, StringComparison.Ordinal))
                    continue;

                tag.Set(TagValue.Good(Evaluate(config, seconds)), WriterId);
            }
        }
    }

    private double Evaluate(SimulatedTagConfig config, double seconds)
    {
        var period = config.PeriodSec <= 0 ? 1.0 : config.PeriodSec;
        var phase = seconds % period / period;          // 0..1
        var span = config.Max - config.Min;

        var unit = config.Waveform.ToLowerInvariant() switch
        {
            "sine" => (Math.Sin(phase * 2 * Math.PI) + 1) * 0.5,
            "triangle" => phase < 0.5 ? phase * 2 : 2 - phase * 2,
            "sawtooth" or "ramp" => phase,
            "square" => phase < 0.5 ? 0.0 : 1.0,
            "random" => _random.NextDouble(),
            "constant" => 1.0,
            _ => phase
        };

        return config.Min + unit * span;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
