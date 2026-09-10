using ModbusBridge.Core.Config;
using ModbusBridge.Core.Data;
using ModbusBridge.Core.Diagnostics;
using ModbusBridge.Core.Modbus;
using ModbusBridge.Core.Tags;

namespace ModbusBridge.Core.Engine;

public enum DeviceState
{
    Disabled,
    Offline,
    Connecting,
    Online,
    Faulted
}

/// <summary>Live counters for one device, polled by the UI.</summary>
public sealed class DeviceStatistics
{
    public long Polls;
    public long Writes;
    public long Errors;
    public long Timeouts;
    /// <summary>Round-trip time of the most recent request, in milliseconds.</summary>
    public double LastLatencyMs;

    /// <summary>Exponentially weighted mean round-trip time.</summary>
    public double AverageLatencyMs;

    /// <summary>Best and worst round-trip seen since the last <see cref="ResetLatency"/>.</summary>
    public double MinLatencyMs = double.NaN;
    public double MaxLatencyMs = double.NaN;

    /// <summary>Mean absolute deviation from the average - how steady the link is.</summary>
    public double JitterMs;

    /// <summary>
    /// Wall-clock time between the start of consecutive complete read cycles. This is the number
    /// that matters for input lag: it is how long a PLC contact can sit unnoticed before we see it.
    /// </summary>
    public double CycleTimeMs;
    public double MaxCycleTimeMs = double.NaN;

    public DateTime? LastSuccessLocal;
    public DateTime? LastErrorLocal;
    public string? LastErrorMessage;

    /// <summary>Completed read cycles per second, averaged over the last second.</summary>
    public double PollsPerSecond;

    internal long PollsAtLastSample;
    internal long LastSampleTicks;

    public void RecordLatency(double ms)
    {
        LastLatencyMs = ms;
        AverageLatencyMs = AverageLatencyMs <= 0 ? ms : AverageLatencyMs * 0.9 + ms * 0.1;
        JitterMs = JitterMs * 0.9 + Math.Abs(ms - AverageLatencyMs) * 0.1;
        if (double.IsNaN(MinLatencyMs) || ms < MinLatencyMs) MinLatencyMs = ms;
        if (double.IsNaN(MaxLatencyMs) || ms > MaxLatencyMs) MaxLatencyMs = ms;
    }

    public void RecordCycle(double ms)
    {
        CycleTimeMs = ms;
        if (double.IsNaN(MaxCycleTimeMs) || ms > MaxCycleTimeMs) MaxCycleTimeMs = ms;
    }

    /// <summary>Clears the min/max high-water marks so the UI can re-measure on demand.</summary>
    public void ResetLatency()
    {
        MinLatencyMs = double.NaN;
        MaxLatencyMs = double.NaN;
        MaxCycleTimeMs = double.NaN;
        JitterMs = 0;
    }
}

internal sealed class ReadGroupRuntime
{
    public required ReadGroupConfig Config { get; init; }
    public required byte UnitId { get; init; }
    public required int WireStart { get; init; }
    public required bool IsBitArea { get; init; }
    public ushort[] Registers = Array.Empty<ushort>();
    public bool[] Bits = Array.Empty<bool>();
    public List<BoundPoint> Points { get; } = new();

    /// <summary>Pre-split requests honouring the device's per-request limits.</summary>
    public List<(int Offset, int Count)> Chunks { get; } = new();

    public long NextDueTicks;
    public bool Failing;

    /// <summary>Time on the wire for this group's last complete read, including every chunk.</summary>
    public double LastLatencyMs;

    /// <summary>Actual gap between the last two reads of this group - the real, achieved poll rate.</summary>
    public double LastCycleMs;

    public long LastStartTicks = -1;
}

/// <summary>Immutable snapshot of a read group's timing, for the UI's latency readout.</summary>
public sealed record ReadGroupTiming(
    string Name,
    ModbusArea Area,
    int StartAddress,
    int Count,
    int ConfiguredIntervalMs,
    double LastLatencyMs,
    double ActualCycleMs,
    bool Failing);

internal sealed class WriteGroupRuntime
{
    public required WriteGroupConfig Config { get; init; }
    public required byte UnitId { get; init; }
    public required int WireStart { get; init; }
    public required int Span { get; init; }
    public required bool IsBitArea { get; init; }
    public ushort[] Registers = Array.Empty<ushort>();
    public bool[] Bits = Array.Empty<bool>();
    public List<BoundPoint> Points { get; } = new();

    public long NextPeriodicTicks;
    public long NextAllowedTicks;
    public bool EverWritten;
}

/// <summary>
/// Owns the connection to one PLC: schedules read groups, pushes write groups, runs the watchdog
/// and keeps the tag bus in sync with the device's online state.
/// </summary>
public sealed class DeviceRunner : IAsyncDisposable
{
    private readonly ModbusClientConfig _config;
    private readonly TagBus _bus;
    private readonly string _writerId;
    private readonly List<ReadGroupRuntime> _readGroups = new();
    private readonly List<WriteGroupRuntime> _writeGroups = new();

    private ModbusTcpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _consecutiveFailures;

    private ushort _watchdogCounter;
    private long _watchdogNextTicks;
    private ushort _lastWatchdogEcho;
    private long _watchdogLastChangeTicks;
    private TagEntry? _watchdogHealthTag;

    public DeviceRunner(ModbusClientConfig config, TagBus bus)
    {
        _config = config;
        _bus = bus;
        _writerId = $"client:{config.Id}";
        Build();
    }

    public string Id => _config.Id;
    public string Name => _config.Name;
    public string Endpoint => $"{_config.Host}:{_config.Port}";
    public ModbusClientConfig Config => _config;
    public DeviceState State { get; private set; } = DeviceState.Offline;
    public DeviceStatistics Statistics { get; } = new();
    public int MappedPointCount { get; private set; }

    /// <summary>True while the watchdog echo is moving (or the watchdog is disabled).</summary>
    public bool LinkHealthy { get; private set; } = true;

    private void Build()
    {
        foreach (var group in _config.ReadGroups.Where(g => g.Enabled))
        {
            var isBitArea = group.Area is ModbusArea.Coil or ModbusArea.DiscreteInput;
            var wireStart = group.StartAddress - _config.BaseFor(group.Area);
            if (wireStart < 0)
            {
                Log.Warn(_writerId, $"Read group '{group.Name}' starts below the address base; skipped.");
                continue;
            }

            var runtime = new ReadGroupRuntime
            {
                Config = group,
                UnitId = group.UnitId ?? _config.UnitId,
                WireStart = wireStart,
                IsBitArea = isBitArea,
                Registers = isBitArea ? Array.Empty<ushort>() : new ushort[group.Count],
                Bits = isBitArea ? new bool[group.Count] : Array.Empty<bool>()
            };

            var maxPerRequest = isBitArea
                ? Math.Clamp(_config.MaxCoilsPerRead, 1, ModbusLimits.MaxReadCoils)
                : Math.Clamp(_config.MaxRegistersPerRead, 1, ModbusLimits.MaxReadRegisters);

            for (var offset = 0; offset < group.Count; offset += maxPerRequest)
                runtime.Chunks.Add((offset, Math.Min(maxPerRequest, group.Count - offset)));

            foreach (var point in group.Points.Where(p => p.Enabled))
                runtime.Points.Add(Bind(point, wireStart));

            MappedPointCount += runtime.Points.Count;
            _readGroups.Add(runtime);
        }

        foreach (var group in _config.WriteGroups.Where(g => g.Enabled))
        {
            var isBitArea = group.Area is ModbusArea.Coil or ModbusArea.DiscreteInput;
            var wireStart = group.StartAddress - _config.BaseFor(group.Area);
            if (wireStart < 0)
            {
                Log.Warn(_writerId, $"Write group '{group.Name}' starts below the address base; skipped.");
                continue;
            }

            var enabledPoints = group.Points.Where(p => p.Enabled).ToList();
            var span = group.Count > 0
                ? group.Count
                : enabledPoints.Count == 0 ? 0 : enabledPoints.Max(p => p.Offset + p.Size);
            if (span <= 0) continue;

            var runtime = new WriteGroupRuntime
            {
                Config = group,
                UnitId = group.UnitId ?? _config.UnitId,
                WireStart = wireStart,
                Span = span,
                IsBitArea = isBitArea,
                Registers = isBitArea ? Array.Empty<ushort>() : new ushort[span],
                Bits = isBitArea ? new bool[span] : Array.Empty<bool>()
            };

            foreach (var point in enabledPoints)
                runtime.Points.Add(Bind(point, wireStart));

            MappedPointCount += runtime.Points.Count;
            _writeGroups.Add(runtime);
        }

        if (_config.Watchdog is { Enabled: true, HealthTag: { Length: > 0 } healthTag })
            _watchdogHealthTag = _bus.GetOrAdd(_config.TagPrefix + healthTag);
    }

    private BoundPoint Bind(PointConfig point, int groupWireStart)
    {
        var tag = _bus.GetOrAdd(_config.TagPrefix + point.Tag);
        tag.Description ??= point.Description;
        tag.Units ??= point.Units;
        if (point.DataType != PointDataType.Bool) tag.DataType = point.DataType;

        return new BoundPoint
        {
            Config = point,
            Tag = tag,
            WireAddress = groupWireStart + point.Offset,
            Size = point.Size,
            WordOrder = point.WordOrder ?? _config.WordOrder,
            ByteOrder = point.ByteOrder ?? _config.ByteOrder
        };
    }

    public void Start()
    {
        if (_loop is not null) return;
        if (!_config.Enabled)
        {
            State = DeviceState.Disabled;
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_loop is null) return;
        _cts?.Cancel();
        try { await _loop.ConfigureAwait(false); } catch { }
        _loop = null;
        _cts?.Dispose();
        _cts = null;
        _client?.Dispose();
        _client = null;
        State = DeviceState.Offline;
        _bus.MarkSourceBad(_writerId);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var reconnectDelay = _config.ReconnectDelayMs;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                State = DeviceState.Connecting;
                Log.Info(_writerId, $"Connecting to {Endpoint} ...");

                _client = new ModbusTcpClient(_config.Host, _config.Port)
                {
                    ConnectTimeoutMs = _config.ConnectTimeoutMs,
                    ResponseTimeoutMs = _config.ResponseTimeoutMs
                };
                await _client.ConnectAsync(ct).ConfigureAwait(false);

                State = DeviceState.Online;
                _consecutiveFailures = 0;
                reconnectDelay = _config.ReconnectDelayMs;
                ResetSchedules();
                Log.Info(_writerId, $"Connected to {Endpoint}.");

                await ServiceLoopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RecordError(ex);
                State = DeviceState.Offline;
                _bus.MarkSourceBad(_writerId);
                Log.Warn(_writerId, $"{Endpoint}: {ex.Message}");
            }
            finally
            {
                _client?.Dispose();
                _client = null;
            }

            if (ct.IsCancellationRequested) break;

            try { await Task.Delay(reconnectDelay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            reconnectDelay = Math.Min(reconnectDelay * 2, _config.MaxReconnectDelayMs);
        }

        State = DeviceState.Offline;
        _bus.MarkSourceBad(_writerId);
    }

    private void ResetSchedules()
    {
        var now = Clock.Ticks;
        foreach (var group in _readGroups)
        {
            group.NextDueTicks = now;
            group.LastStartTicks = -1;
        }
        foreach (var group in _writeGroups)
        {
            group.NextPeriodicTicks = now;
            group.NextAllowedTicks = now;
            group.EverWritten = false;
            foreach (var point in group.Points) point.LastWrittenValue = double.NaN;
        }
        _watchdogNextTicks = now;
        _watchdogLastChangeTicks = now;
        Statistics.LastSampleTicks = now;
        Statistics.PollsAtLastSample = Statistics.Polls;
    }

    /// <summary>Runs until the link fails; each pass services whatever is due.</summary>
    private async Task ServiceLoopAsync(CancellationToken ct)
    {
        var client = _client!;

        while (!ct.IsCancellationRequested)
        {
            var now = Clock.Ticks;
            var didWork = false;

            foreach (var group in _readGroups)
            {
                if (now < group.NextDueTicks) continue;
                await PollAsync(client, group, ct).ConfigureAwait(false);

                var interval = Clock.FromMs(Math.Max(0.1, group.Config.PollIntervalMs));

                // Advance from the previous deadline rather than from "now", so a slow poll does not
                // permanently push the schedule out. If we have fallen more than one interval
                // behind, resynchronise instead of trying to catch up in a burst.
                group.NextDueTicks += interval;
                if (group.NextDueTicks < Clock.Ticks) group.NextDueTicks = Clock.Ticks + interval;

                didWork = true;
                await ThrottleAsync(ct).ConfigureAwait(false);
            }

            foreach (var group in _writeGroups)
            {
                if (await PushAsync(client, group, ct).ConfigureAwait(false))
                {
                    didWork = true;
                    await ThrottleAsync(ct).ConfigureAwait(false);
                }
            }

            if (_config.Watchdog.Enabled && Clock.Ticks >= _watchdogNextTicks)
            {
                await ServiceWatchdogAsync(client, ct).ConfigureAwait(false);
                _watchdogNextTicks = Clock.Ticks + Clock.FromMs(Math.Max(1, _config.Watchdog.IntervalMs));
                didWork = true;
            }

            UpdateRate();

            if (!didWork)
            {
                // Sleep until the next due item rather than spinning the CPU.
                var wait = NextDueDelayMs();
                if (wait > 0) await PreciseDelay.WaitAsync(wait, ct).ConfigureAwait(false);
                else await Task.Yield();
            }
        }
    }

    /// <summary>Milliseconds until the soonest scheduled item, capped so cancellation stays responsive.</summary>
    private double NextDueDelayMs()
    {
        var now = Clock.Ticks;
        var soonest = long.MaxValue;

        foreach (var group in _readGroups)
            soonest = Math.Min(soonest, group.NextDueTicks);
        foreach (var group in _writeGroups)
        {
            if (group.Config.Mode != WriteMode.OnChange)
                soonest = Math.Min(soonest, group.NextPeriodicTicks);

            // NextAllowedTicks is a rate-limit FLOOR, not a due time, and once it passes it stays
            // in the past - so this pins the computed wait at zero and the caller falls into a
            // Task.Yield() spin. That LOOKS like a bug and was "fixed" once; do not repeat it.
            // The spin is what holds a 10 ms poll at 10.0 ms, because Task.Delay rounds up to the
            // ~15.6 ms system tick. Skipping it drops the interval to 15.6 ms and fails the smoke
            // test. Sub-tick polling on Windows costs roughly a core; that is the trade, and the
            // lever is pollIntervalMs, not this line.
            if (group.NextAllowedTicks > now)
                soonest = Math.Min(soonest, group.NextAllowedTicks);
        }
        if (_config.Watchdog.Enabled) soonest = Math.Min(soonest, _watchdogNextTicks);

        if (soonest == long.MaxValue) return 25;
        return Math.Clamp(Clock.ToMs(soonest - now), 0, 25);
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        if (_config.InterRequestDelayMs > 0)
            await PreciseDelay.WaitAsync(_config.InterRequestDelayMs, ct).ConfigureAwait(false);
    }

    /// <summary>Per-read-group timing, so the UI can show which group is costing the most latency.</summary>
    public IReadOnlyList<ReadGroupTiming> ReadGroupTimings() =>
        _readGroups.Select(g => new ReadGroupTiming(
            g.Config.Name, g.Config.Area, g.Config.StartAddress, g.Config.Count,
            g.Config.PollIntervalMs, g.LastLatencyMs, g.LastCycleMs, g.Failing)).ToList();

    private async Task PollAsync(ModbusTcpClient client, ReadGroupRuntime group, CancellationToken ct)
    {
        var startTicks = Clock.Ticks;
        if (group.LastStartTicks >= 0)
        {
            group.LastCycleMs = Clock.ToMs(startTicks - group.LastStartTicks);
            Statistics.RecordCycle(group.LastCycleMs);
        }
        group.LastStartTicks = startTicks;

        try
        {
            foreach (var (offset, count) in group.Chunks)
            {
                var address = (ushort)(group.WireStart + offset);
                switch (group.Config.Area)
                {
                    case ModbusArea.Coil:
                    {
                        var bits = await client.ReadCoilsAsync(group.UnitId, address, (ushort)count, ct).ConfigureAwait(false);
                        bits.CopyTo(group.Bits, offset);
                        break;
                    }
                    case ModbusArea.DiscreteInput:
                    {
                        var bits = await client.ReadDiscreteInputsAsync(group.UnitId, address, (ushort)count, ct).ConfigureAwait(false);
                        bits.CopyTo(group.Bits, offset);
                        break;
                    }
                    case ModbusArea.HoldingRegister:
                    {
                        var registers = await client.ReadHoldingRegistersAsync(group.UnitId, address, (ushort)count, ct).ConfigureAwait(false);
                        registers.CopyTo(group.Registers, offset);
                        break;
                    }
                    case ModbusArea.InputRegister:
                    {
                        var registers = await client.ReadInputRegistersAsync(group.UnitId, address, (ushort)count, ct).ConfigureAwait(false);
                        registers.CopyTo(group.Registers, offset);
                        break;
                    }
                }
            }

            PublishGroup(group);

            group.LastLatencyMs = Clock.MsSince(startTicks);
            group.Failing = false;
            _consecutiveFailures = 0;
            Statistics.Polls++;
            Statistics.RecordLatency(client.LastRoundTripMs);
            Statistics.LastSuccessLocal = DateTime.Now;
        }
        catch (ModbusProtocolException ex)
        {
            // An addressing mistake is permanent - log once and stop hammering the device.
            if (!group.Failing)
            {
                group.Failing = true;
                Log.Error(_writerId, $"Read group '{group.Config.Name}' " +
                                     $"({group.Config.Area} {group.Config.StartAddress}+{group.Config.Count}): {ex.Message}");
            }
            RecordError(ex);
            MarkGroupBad(group);
            group.NextDueTicks = Clock.Ticks + Clock.FromMs(Math.Max(1000, group.Config.PollIntervalMs));
        }
    }

    private void PublishGroup(ReadGroupRuntime group)
    {
        foreach (var point in group.Points)
        {
            var offset = point.WireAddress - group.WireStart;

            if (group.IsBitArea)
            {
                var engineering = point.Config.RawToEngineering(group.Bits[offset] ? 1d : 0d);
                point.Tag.Set(TagValue.Good(engineering), _writerId);
            }
            else if (point.DataType == PointDataType.String)
            {
                var text = ValueCodec.DecodeString(group.Registers.AsSpan(offset), point.Size, point.ByteOrder);
                point.Tag.Set(TagValue.GoodText(text), _writerId);
            }
            else
            {
                var raw = ValueCodec.Decode(group.Registers.AsSpan(offset), point.DataType,
                                            point.WordOrder, point.ByteOrder, point.BitIndex);
                var engineering = point.Config.RawToEngineering(raw);
                point.Tag.Set(TagValue.Good(engineering), _writerId);
            }
        }
    }

    private static void MarkGroupBad(ReadGroupRuntime group)
    {
        foreach (var point in group.Points)
            point.Tag.DemoteQuality(TagQuality.Bad);
    }

    /// <summary>Refreshes the write image from tags and pushes it if due. Returns true if it wrote.</summary>
    private async Task<bool> PushAsync(ModbusTcpClient client, WriteGroupRuntime group, CancellationToken ct)
    {
        var now = Clock.Ticks;
        if (now < group.NextAllowedTicks) return false;

        var changedLow = int.MaxValue;
        var changedHigh = -1;

        foreach (var point in group.Points)
        {
            var value = point.Tag.Value;

            // Never push a value we have not received yet, and never echo a value this same device
            // just gave us - that would create a write loop on a shared register.
            if (value.Quality == TagQuality.Never) continue;
            if (string.Equals(point.Tag.LastWriterId, _writerId, StringComparison.Ordinal)) continue;

            var engineering = value.Number;
            var previous = point.LastWrittenValue;
            var deadband = point.Config.Deadband;
            var changed = double.IsNaN(previous) ||
                          (deadband > 0 ? Math.Abs(engineering - previous) > deadband
                                        : engineering != previous);
            if (!changed) continue;

            point.LastWrittenValue = engineering;
            var offset = point.WireAddress - group.WireStart;

            if (group.IsBitArea)
            {
                group.Bits[offset] = point.Config.EngineeringToRaw(engineering) != 0d;
            }
            else if (point.DataType == PointDataType.String)
            {
                ValueCodec.EncodeString(value.Text, group.Registers.AsSpan(offset), point.Size, point.ByteOrder);
            }
            else
            {
                var raw = point.Config.EngineeringToRaw(engineering);
                ValueCodec.Encode(raw, group.Registers.AsSpan(offset), point.DataType,
                                  point.WordOrder, point.ByteOrder, point.BitIndex);
            }

            changedLow = Math.Min(changedLow, offset);
            changedHigh = Math.Max(changedHigh, offset + point.Size - 1);
        }

        var hasChange = changedHigh >= 0;
        var periodicDue = group.Config.Mode is WriteMode.Periodic or WriteMode.OnChangeAndPeriodic &&
                          now >= group.NextPeriodicTicks;

        // The first push after connecting must send everything, so the PLC starts from a known state.
        var forceWholeBlock = !group.EverWritten || group.Config.AlwaysWriteWholeBlock || periodicDue;

        if (!hasChange && !periodicDue) return false;
        if (group.Config.Mode == WriteMode.Periodic && !periodicDue) return false;

        var low = forceWholeBlock ? 0 : changedLow;
        var high = forceWholeBlock ? group.Span - 1 : changedHigh;

        try
        {
            await WriteRangeAsync(client, group, low, high - low + 1, ct).ConfigureAwait(false);
            group.EverWritten = true;
            Statistics.Writes++;
            Statistics.RecordLatency(client.LastRoundTripMs);
            Statistics.LastSuccessLocal = DateTime.Now;

            if (periodicDue)
                group.NextPeriodicTicks = Clock.Ticks + Clock.FromMs(Math.Max(1, group.Config.PeriodMs));
            if (group.Config.Mode is WriteMode.OnChange or WriteMode.OnChangeAndPeriodic)
                group.NextAllowedTicks = Clock.Ticks + Clock.FromMs(Math.Max(0, group.Config.PeriodMs));

            return true;
        }
        catch (ModbusProtocolException ex)
        {
            Log.Error(_writerId, $"Write group '{group.Config.Name}': {ex.Message}");
            RecordError(ex);
            group.NextAllowedTicks = Clock.Ticks + Clock.FromMs(1000);
            foreach (var point in group.Points) point.LastWrittenValue = double.NaN;
            return false;
        }
    }

    private async Task WriteRangeAsync(ModbusTcpClient client, WriteGroupRuntime group,
                                       int offset, int count, CancellationToken ct)
    {
        var maxPerRequest = group.IsBitArea
            ? Math.Clamp(_config.MaxCoilsPerWrite, 1, ModbusLimits.MaxWriteCoils)
            : Math.Clamp(_config.MaxRegistersPerWrite, 1, ModbusLimits.MaxWriteRegisters);

        for (var written = 0; written < count; written += maxPerRequest)
        {
            var chunkOffset = offset + written;
            var chunkCount = Math.Min(maxPerRequest, count - written);
            var address = (ushort)(group.WireStart + chunkOffset);

            if (group.IsBitArea)
            {
                if (chunkCount == 1 && !group.Config.UseMultipleWrite)
                    await client.WriteSingleCoilAsync(group.UnitId, address, group.Bits[chunkOffset], ct).ConfigureAwait(false);
                else
                    await client.WriteMultipleCoilsAsync(group.UnitId, address,
                        group.Bits[chunkOffset..(chunkOffset + chunkCount)], ct).ConfigureAwait(false);
            }
            else
            {
                if (chunkCount == 1 && !group.Config.UseMultipleWrite)
                    await client.WriteSingleRegisterAsync(group.UnitId, address, group.Registers[chunkOffset], ct).ConfigureAwait(false);
                else
                    await client.WriteMultipleRegistersAsync(group.UnitId, address,
                        group.Registers[chunkOffset..(chunkOffset + chunkCount)], ct).ConfigureAwait(false);
            }

            if (written + maxPerRequest < count) await ThrottleAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task ServiceWatchdogAsync(ModbusTcpClient client, CancellationToken ct)
    {
        var watchdog = _config.Watchdog;
        try
        {
            _watchdogCounter = unchecked((ushort)(_watchdogCounter + 1));
            var writeAddress = (ushort)(watchdog.WriteAddress - _config.BaseFor(watchdog.WriteArea));

            if (watchdog.WriteArea == ModbusArea.Coil)
                await client.WriteSingleCoilAsync(_config.UnitId, writeAddress, (_watchdogCounter & 1) != 0, ct).ConfigureAwait(false);
            else
                await client.WriteSingleRegisterAsync(_config.UnitId, writeAddress, _watchdogCounter, ct).ConfigureAwait(false);

            if (watchdog.ReadAddress >= 0)
            {
                var readAddress = (ushort)(watchdog.ReadAddress - _config.BaseFor(watchdog.ReadArea));
                ushort echo;
                if (watchdog.ReadArea is ModbusArea.Coil or ModbusArea.DiscreteInput)
                {
                    var bits = watchdog.ReadArea == ModbusArea.Coil
                        ? await client.ReadCoilsAsync(_config.UnitId, readAddress, 1, ct).ConfigureAwait(false)
                        : await client.ReadDiscreteInputsAsync(_config.UnitId, readAddress, 1, ct).ConfigureAwait(false);
                    echo = bits[0] ? (ushort)1 : (ushort)0;
                }
                else
                {
                    var registers = watchdog.ReadArea == ModbusArea.HoldingRegister
                        ? await client.ReadHoldingRegistersAsync(_config.UnitId, readAddress, 1, ct).ConfigureAwait(false)
                        : await client.ReadInputRegistersAsync(_config.UnitId, readAddress, 1, ct).ConfigureAwait(false);
                    echo = registers[0];
                }

                if (echo != _lastWatchdogEcho)
                {
                    _lastWatchdogEcho = echo;
                    _watchdogLastChangeTicks = Clock.Ticks;
                }

                var silentMs = Clock.MsSince(_watchdogLastChangeTicks);
                var healthy = silentMs <= watchdog.TimeoutMs;
                if (healthy != LinkHealthy)
                {
                    LinkHealthy = healthy;
                    Log.Warn(_writerId, healthy
                        ? "Watchdog echo recovered."
                        : $"Watchdog echo has not changed for {silentMs:0} ms - link considered unhealthy.");
                }
            }

            _watchdogHealthTag?.Set(TagValue.Good(LinkHealthy), _writerId);
        }
        catch (ModbusProtocolException ex)
        {
            Log.Error(_writerId, $"Watchdog: {ex.Message}");
            RecordError(ex);
            LinkHealthy = false;
            _watchdogHealthTag?.Set(TagValue.Good(false), _writerId);
        }
    }

    private void UpdateRate()
    {
        var now = Clock.Ticks;
        var elapsedMs = Clock.ToMs(now - Statistics.LastSampleTicks);
        if (elapsedMs < 1000) return;

        Statistics.PollsPerSecond = (Statistics.Polls - Statistics.PollsAtLastSample) * 1000.0 / elapsedMs;
        Statistics.PollsAtLastSample = Statistics.Polls;
        Statistics.LastSampleTicks = now;
    }

    private void RecordError(Exception ex)
    {
        Statistics.Errors++;
        if (ex is TimeoutException) Statistics.Timeouts++;
        Statistics.LastErrorLocal = DateTime.Now;
        Statistics.LastErrorMessage = ex.Message;

        if (++_consecutiveFailures >= _config.FailuresBeforeOffline)
        {
            State = DeviceState.Faulted;
            _bus.MarkSourceBad(_writerId);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
