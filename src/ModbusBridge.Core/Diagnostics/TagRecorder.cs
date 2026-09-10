using System.Globalization;
using System.Text;
using ModbusBridge.Core.Config;
using ModbusBridge.Core.Tags;

namespace ModbusBridge.Core.Diagnostics;

/// <summary>
/// Writes selected tags to CSV over time, so a session can be captured and replayed later - which
/// is how an HMI screen or a vJoy mapping gets exercised without the game or the PLC present.
///
/// Columns are fixed when recording starts. A tag created afterwards is not added mid-file,
/// because a CSV whose column count changes partway is painful for everything that reads it.
/// </summary>
public sealed class TagRecorder : IAsyncDisposable
{
    public const string WriterId = "recorder";

    private readonly RecordingConfig _config;
    private readonly TagBus _bus;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private StreamWriter? _writer;
    private TagEntry[] _tags = Array.Empty<TagEntry>();
    private long[] _lastVersions = Array.Empty<long>();
    private long _startTicks;

    public string? Path { get; private set; }
    public long RowsWritten { get; private set; }

    public TagRecorder(RecordingConfig config, TagBus bus)
    {
        _config = config;
        _bus = bus;
    }

    public bool IsRunning => _loop is not null;

    /// <summary>Glob matching with '*' anywhere, so "sim.*" or "*.estop" both work.</summary>
    public static bool Matches(string pattern, string name)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        if (pattern == "*") return true;

        var parts = pattern.Split('*');
        var index = 0;

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0) continue;

            if (i == 0)
            {
                if (!name.StartsWith(part, StringComparison.OrdinalIgnoreCase)) return false;
                index = part.Length;
                continue;
            }

            if (i == parts.Length - 1 && !pattern.EndsWith('*'))
                // The suffix must begin at or after what the prefix already consumed, or a
                // pattern like "ab*ab" matches "ab" by counting the same characters twice.
                return name.Length - part.Length >= index &&
                       name.EndsWith(part, StringComparison.OrdinalIgnoreCase);

            var found = name.IndexOf(part, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            index = found + part.Length;
        }

        return true;
    }

    public void Start()
    {
        if (_loop is not null) return;

        var selected = _bus.Snapshot()
                           .Where(t => _config.Tags.Any(p => Matches(p, t.Name)))
                           .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                           .ToArray();

        if (selected.Length == 0)
        {
            Log.Warn("recorder", $"No tags match {string.Join(", ", _config.Tags)}; not recording.");
            return;
        }

        // Relative paths resolve against the data directory, not the process working directory.
        var directory = Paths.Resolve(_config.Directory, "recordings");
        Directory.CreateDirectory(directory);

        Path = string.IsNullOrWhiteSpace(_config.FileName)
            ? System.IO.Path.Combine(directory, $"tags-{DateTime.Now:yyyyMMdd-HHmmss}.csv")
            : System.IO.Path.Combine(directory, _config.FileName);

        _tags = selected;
        _lastVersions = new long[selected.Length];
        _writer = new StreamWriter(Path, append: false, Encoding.UTF8);

        // Elapsed milliseconds rather than wall-clock: replay only needs relative timing, and it
        // survives the file being recorded on one machine and replayed on another.
        _writer.Write("elapsedMs");
        foreach (var tag in selected) { _writer.Write(','); _writer.Write(Escape(tag.Name)); }
        _writer.Write('\n');

        _startTicks = Clock.Ticks;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));

        Log.Info("recorder", $"Recording {selected.Length} tag(s) to {Path} every {_config.IntervalMs} ms.");
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

        if (_writer is not null)
        {
            await _writer.FlushAsync().ConfigureAwait(false);
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writer = null;
        }

        Log.Info("recorder", $"Stopped after {RowsWritten} row(s): {Path}");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(CancellationToken ct)
    {
        var interval = Math.Max(1, _config.IntervalMs);

        while (!ct.IsCancellationRequested)
        {
            try { WriteRow(); }
            catch (Exception ex) { Log.Warn("recorder", $"Row failed: {ex.Message}"); }

            if (_config.MaxRows > 0 && RowsWritten >= _config.MaxRows)
            {
                Log.Info("recorder", $"Reached maxRows ({_config.MaxRows}); stopping.");
                break;
            }

            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void WriteRow()
    {
        if (_writer is null) return;

        if (_config.OnChangeOnly)
        {
            var moved = false;
            for (var i = 0; i < _tags.Length; i++)
            {
                if (_tags[i].Version == _lastVersions[i]) continue;
                moved = true;
                break;
            }
            if (!moved) return;
        }

        for (var i = 0; i < _tags.Length; i++) _lastVersions[i] = _tags[i].Version;

        _writer.Write(Clock.MsSince(_startTicks).ToString("0", CultureInfo.InvariantCulture));

        foreach (var tag in _tags)
        {
            _writer.Write(',');
            var value = tag.Value;
            _writer.Write(value.Text is not null
                ? Escape(value.Text ?? "")
                : value.Number.ToString("R", CultureInfo.InvariantCulture));
        }

        _writer.Write('\n');
        RowsWritten++;

        // Flushed regularly so a recording survives the process being killed, which is how a
        // long capture usually ends.
        if (RowsWritten % 50 == 0) _writer.Flush();
    }

    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n')) return value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}
