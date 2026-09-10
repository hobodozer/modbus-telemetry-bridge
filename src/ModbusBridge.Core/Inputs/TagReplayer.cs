using System.Globalization;
using ModbusBridge.Core.Config;
using ModbusBridge.Core.Data;
using ModbusBridge.Core.Diagnostics;
using ModbusBridge.Core.Tags;

namespace ModbusBridge.Core.Inputs;

/// <summary>
/// Publishes a recorded CSV back onto the tag bus at its original timing, so HMI screens, vJoy
/// mappings and register maps can be exercised with no game and no PLC attached.
///
/// It is just another source: it publishes under its own writer id, so the tag monitor shows where
/// a value came from and a real source taking over is visible.
/// </summary>
public sealed class TagReplayer : IAsyncDisposable
{
    public const string WriterId = "replay";

    private readonly ReplayConfig _config;
    private readonly TagBus _bus;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    private string[] _columns = Array.Empty<string>();
    private TagEntry[] _targets = Array.Empty<TagEntry>();
    private List<(double ElapsedMs, string[] Values)> _rows = new();

    public int RowCount => _rows.Count;
    public int ColumnCount => _columns.Length;
    public long Passes { get; private set; }

    public TagReplayer(ReplayConfig config, TagBus bus)
    {
        _config = config;
        _bus = bus;
    }

    public bool IsRunning => _loop is not null;

    /// <summary>Loads the file. Returns false and logs if it is missing or has no usable rows.</summary>
    public bool Load()
    {
        var path = Paths.Resolve(_config.Path, "");
        if (string.IsNullOrWhiteSpace(_config.Path) || !File.Exists(path))
        {
            Log.Warn("replay", $"No recording at '{path}'.");
            return false;
        }

        var lines = File.ReadAllLines(path);
        if (lines.Length < 2)
        {
            Log.Warn("replay", $"'{_config.Path}' has no data rows.");
            return false;
        }

        _columns = SplitCsv(lines[0]).Skip(1).ToArray();   // column 0 is elapsedMs
        _targets = _columns.Select(name => _bus.GetOrAdd(_config.TagPrefix + name)).ToArray();

        _rows = new List<(double, string[])>(lines.Length - 1);
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var fields = SplitCsv(lines[i]);
            if (fields.Count < 1) continue;
            if (!double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var at))
                continue;

            _rows.Add((at, fields.Skip(1).ToArray()));
        }

        if (_rows.Count == 0)
        {
            Log.Warn("replay", $"'{_config.Path}' parsed to zero rows.");
            return false;
        }

        Log.Info("replay", $"Loaded {_rows.Count} row(s) x {_columns.Length} tag(s) from {_config.Path}.");
        return true;
    }

    public void Start()
    {
        if (_loop is not null) return;
        if (_rows.Count == 0 && !Load()) return;

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));

        var speed = _config.Speed <= 0 ? 1.0 : _config.Speed;
        Log.Info("replay", $"Replaying at {speed:0.##}x{(_config.Loop ? ", looping" : "")}.");
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _cts = null;
        _loop = null;
        Log.Info("replay", "Stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(CancellationToken ct)
    {
        var speed = _config.Speed <= 0 ? 1.0 : _config.Speed;

        do
        {
            var started = Clock.Ticks;

            foreach (var (elapsed, values) in _rows)
            {
                if (ct.IsCancellationRequested) return;

                // Schedule against the run's start rather than sleeping row-to-row, so a slow row
                // does not push everything after it later and later.
                var due = elapsed / speed;
                var wait = due - Clock.MsSince(started);
                if (wait > 0)
                {
                    try { await PreciseDelay.WaitAsync(wait, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }

                Publish(values);
            }

            Passes++;
        }
        while (_config.Loop && !ct.IsCancellationRequested);

        Log.Info("replay", $"Reached the end of the recording after {Passes} pass(es).");
    }

    private void Publish(string[] values)
    {
        var count = Math.Min(values.Length, _targets.Length);

        for (var i = 0; i < count; i++)
        {
            var raw = values[i];
            if (raw.Length == 0) continue;

            // A field that is not a number is republished as text, so string tags such as the
            // active game name survive a round trip.
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                _targets[i].Set(TagValue.Good(number), WriterId);
            else
                _targets[i].Set(TagValue.GoodText(raw), WriterId);
        }
    }

    /// <summary>Minimal CSV reader: handles quoted fields and doubled quotes, nothing more exotic.</summary>
    internal static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (quoted)
            {
                if (c != '"') { current.Append(c); continue; }
                if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; continue; }
                quoted = false;
                continue;
            }

            switch (c)
            {
                case '"': quoted = true; break;
                case ',': fields.Add(current.ToString()); current.Clear(); break;
                default: current.Append(c); break;
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
