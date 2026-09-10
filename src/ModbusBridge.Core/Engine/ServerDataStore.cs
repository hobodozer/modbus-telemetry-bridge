using ModbusBridge.Core.Config;
using ModbusBridge.Core.Data;
using ModbusBridge.Core.Diagnostics;
using ModbusBridge.Core.Modbus;
using ModbusBridge.Core.Tags;

namespace ModbusBridge.Core.Engine;

/// <summary>
/// The register image for one configured block. Values are re-encoded from the tag bus lazily -
/// a point is only touched when its tag version or quality moved since the last refresh.
/// </summary>
internal sealed class BlockImage
{
    public required ServerBlockConfig Config { get; init; }
    public required int WireStart { get; init; }
    public required int Size { get; init; }
    public required bool IsBitArea { get; init; }

    public ushort[] Registers { get; init; } = Array.Empty<ushort>();
    public bool[] Bits { get; init; } = Array.Empty<bool>();

    public List<BoundPoint> Points { get; } = new();

    /// <summary>Set when a point in this block is failing under <see cref="StaleBehavior.ModbusException"/>.</summary>
    public bool Failed { get; private set; }

    public int WireEnd => WireStart + Size;
    public bool Contains(int wireAddress) => wireAddress >= WireStart && wireAddress < WireEnd;

    /// <summary>Re-encodes any point whose tag changed. Caller holds the map lock.</summary>
    public void Refresh()
    {
        var failed = false;

        foreach (var point in Points)
        {
            var version = point.Tag.Version;
            var quality = point.Tag.Value.Quality;
            var isStaleSensitive = Config.StaleBehavior != StaleBehavior.HoldLastValue || Config.StaleTimeoutMs > 0;

            // A stale-sensitive point must be re-evaluated every pass because time alone can change it.
            if (!isStaleSensitive && version == point.LastVersion && quality == point.LastQuality)
                continue;

            point.LastVersion = version;
            point.LastQuality = quality;

            var engineering = point.EffectiveValue(Config.StaleBehavior, Config.StaleTimeoutMs, out var pointFailed);
            if (pointFailed) failed = true;

            Write(point, engineering);
        }

        Failed = failed;
    }

    private void Write(BoundPoint point, double engineering)
    {
        var offset = point.WireAddress - WireStart;

        if (IsBitArea)
        {
            Bits[offset] = point.Config.EngineeringToRaw(engineering) != 0d;
            return;
        }

        if (point.DataType == PointDataType.String)
        {
            var text = point.Tag.Value.Text ?? "";
            ValueCodec.EncodeString(text, Registers.AsSpan(offset), point.Size, point.ByteOrder);
            return;
        }

        var raw = point.Config.EngineeringToRaw(engineering);
        ValueCodec.Encode(raw, Registers.AsSpan(offset), point.DataType,
                          point.WordOrder, point.ByteOrder, point.BitIndex);
    }

    /// <summary>Decodes points covered by a client write and publishes them to the tag bus.</summary>
    public int ApplyWrite(int wireStart, ReadOnlySpan<ushort> registers, ReadOnlySpan<bool> bits,
                          string writerId, TagBus bus)
    {
        var applied = 0;
        var wireEnd = wireStart + (IsBitArea ? bits.Length : registers.Length);

        // Overlay the incoming data onto the image first, so partially-covered multi-register
        // points and packed bits decode against the correct surrounding words.
        if (IsBitArea)
        {
            for (var i = 0; i < bits.Length; i++)
            {
                var address = wireStart + i;
                if (Contains(address)) Bits[address - WireStart] = bits[i];
            }
        }
        else
        {
            for (var i = 0; i < registers.Length; i++)
            {
                var address = wireStart + i;
                if (Contains(address)) Registers[address - WireStart] = registers[i];
            }
        }

        foreach (var point in Points)
        {
            if (!point.IsWritable) continue;
            if (point.WireEnd <= wireStart || point.WireAddress >= wireEnd) continue;

            var offset = point.WireAddress - WireStart;

            if (IsBitArea)
            {
                var engineering = point.Config.RawToEngineering(Bits[offset] ? 1d : 0d);
                point.Tag.Set(TagValue.Good(engineering), writerId);
            }
            else if (point.DataType == PointDataType.String)
            {
                var text = ValueCodec.DecodeString(Registers.AsSpan(offset), point.Size, point.ByteOrder);
                point.Tag.Set(TagValue.GoodText(text), writerId);
            }
            else
            {
                var raw = ValueCodec.Decode(Registers.AsSpan(offset), point.DataType,
                                            point.WordOrder, point.ByteOrder, point.BitIndex);
                var engineering = point.Config.RawToEngineering(raw);
                point.Tag.Set(TagValue.Good(engineering), writerId);
            }

            // The value we just published is what the image already holds; do not let the next
            // Refresh() re-encode it from a stale version number.
            point.LastVersion = point.Tag.Version;
            point.LastQuality = point.Tag.Value.Quality;
            applied++;
        }

        return applied;
    }
}

/// <summary>All four areas of one unit id.</summary>
internal sealed class UnitMap
{
    public required ServerMapConfig Config { get; init; }
    public Dictionary<ModbusArea, List<BlockImage>> Areas { get; } = new();
    public readonly object Gate = new();

    public List<BlockImage> Blocks(ModbusArea area) =>
        Areas.TryGetValue(area, out var blocks) ? blocks : EmptyBlocks;

    private static readonly List<BlockImage> EmptyBlocks = new();
}

/// <summary>
/// Serves a <see cref="ModbusServerConfig"/> out of the tag bus. Reads project tags into the
/// register image on demand; writes from a client are decoded straight back onto their tags, which
/// is what lets an HMI button appear as an input everywhere else in the bridge.
/// </summary>
public sealed class ServerDataStore : IModbusDataStore
{
    private readonly ModbusServerConfig _config;
    private readonly TagBus _bus;
    private readonly Dictionary<byte, UnitMap> _units = new();
    private readonly UnitMap? _catchAll;
    private readonly string _writerId;

    public ServerDataStore(ModbusServerConfig config, TagBus bus)
    {
        _config = config;
        _bus = bus;
        _writerId = $"server:{config.Id}";

        foreach (var map in config.Maps.Where(m => m.Enabled))
        {
            var unit = Build(map);
            _units[map.UnitId] = unit;
            if (map.AcceptAnyUnitId) _catchAll ??= unit;
        }
    }

    /// <summary>Total mapped points, for the status display.</summary>
    public int PointCount { get; private set; }

    private UnitMap Build(ServerMapConfig map)
    {
        var unit = new UnitMap { Config = map };

        foreach (var block in map.Blocks.Where(b => b.Enabled))
        {
            var isBitArea = block.Area is ModbusArea.Coil or ModbusArea.DiscreteInput;
            var wireStart = block.StartAddress - _config.BaseFor(block.Area);
            if (wireStart < 0)
            {
                Log.Warn($"server:{_config.Name}",
                         $"Block '{block.Name}' starts below the address base and was skipped.");
                continue;
            }

            var image = new BlockImage
            {
                Config = block,
                WireStart = wireStart,
                Size = block.Size,
                IsBitArea = isBitArea,
                Registers = isBitArea ? Array.Empty<ushort>() : new ushort[block.Size],
                Bits = isBitArea ? new bool[block.Size] : Array.Empty<bool>()
            };

            foreach (var point in block.Points.Where(p => p.Enabled))
            {
                var tagName = map.TagPrefix + point.Tag;
                var tag = _bus.GetOrAdd(tagName);
                tag.Description ??= point.Description;
                tag.Units ??= point.Units;
                if (point.DataType != PointDataType.Bool) tag.DataType = point.DataType;

                image.Points.Add(new BoundPoint
                {
                    Config = point,
                    Tag = tag,
                    WireAddress = wireStart + point.Offset,
                    Size = point.Size,
                    WordOrder = point.WordOrder ?? _config.WordOrder,
                    ByteOrder = point.ByteOrder ?? _config.ByteOrder
                });
                PointCount++;
            }

            if (!unit.Areas.TryGetValue(block.Area, out var list))
                unit.Areas[block.Area] = list = new List<BlockImage>();
            list.Add(image);
        }

        foreach (var list in unit.Areas.Values)
            list.Sort((a, b) => a.WireStart.CompareTo(b.WireStart));

        return unit;
    }

    private UnitMap? Resolve(byte unitId) =>
        _units.TryGetValue(unitId, out var unit) ? unit : _catchAll;

    public bool HasUnit(byte unitId) => Resolve(unitId) is not null;

    // ---- Reads ----------------------------------------------------------------------------

    public byte ReadCoils(byte unitId, ushort start, ushort count, Span<bool> destination) =>
        ReadBits(unitId, ModbusArea.Coil, start, count, destination);

    public byte ReadDiscreteInputs(byte unitId, ushort start, ushort count, Span<bool> destination) =>
        ReadBits(unitId, ModbusArea.DiscreteInput, start, count, destination);

    public byte ReadHoldingRegisters(byte unitId, ushort start, ushort count, Span<ushort> destination) =>
        ReadRegisters(unitId, ModbusArea.HoldingRegister, start, count, destination);

    public byte ReadInputRegisters(byte unitId, ushort start, ushort count, Span<ushort> destination) =>
        ReadRegisters(unitId, ModbusArea.InputRegister, start, count, destination);

    private byte ReadBits(byte unitId, ModbusArea area, int start, int count, Span<bool> destination)
    {
        var unit = Resolve(unitId);
        if (unit is null) return ModbusExceptionCode.GatewayPathUnavailable;

        lock (unit.Gate)
        {
            var blocks = unit.Blocks(area);
            for (var i = 0; i < count; i++)
            {
                var address = start + i;
                var block = blocks.FirstOrDefault(b => b.Contains(address));
                if (block is null) return ModbusExceptionCode.IllegalDataAddress;

                block.Refresh();
                if (block.Failed) return ModbusExceptionCode.ServerDeviceFailure;

                destination[i] = block.Bits[address - block.WireStart];
            }
        }
        return 0;
    }

    private byte ReadRegisters(byte unitId, ModbusArea area, int start, int count, Span<ushort> destination)
    {
        var unit = Resolve(unitId);
        if (unit is null) return ModbusExceptionCode.GatewayPathUnavailable;

        lock (unit.Gate)
        {
            var blocks = unit.Blocks(area);
            for (var i = 0; i < count; i++)
            {
                var address = start + i;
                var block = blocks.FirstOrDefault(b => b.Contains(address));
                if (block is null) return ModbusExceptionCode.IllegalDataAddress;

                block.Refresh();
                if (block.Failed) return ModbusExceptionCode.ServerDeviceFailure;

                destination[i] = block.Registers[address - block.WireStart];
            }
        }
        return 0;
    }

    // ---- Writes ---------------------------------------------------------------------------

    public byte WriteCoils(byte unitId, ushort start, ReadOnlySpan<bool> values, ModbusRequestContext context)
    {
        var unit = Resolve(unitId);
        if (unit is null) return ModbusExceptionCode.GatewayPathUnavailable;

        lock (unit.Gate)
        {
            var blocks = unit.Blocks(ModbusArea.Coil);
            var end = start + values.Length;
            var touched = blocks.Where(b => b.WireStart < end && b.WireEnd > start).ToList();
            if (touched.Count == 0) return ModbusExceptionCode.IllegalDataAddress;

            var applied = 0;
            foreach (var block in touched)
                applied += block.ApplyWrite(start, ReadOnlySpan<ushort>.Empty, values, _writerId, _bus);

            if (applied == 0)
            {
                Log.Debug($"server:{_config.Name}",
                          $"{context.RemoteEndPoint} wrote coils {start}..{end - 1} but no point there is writable.");
                return ModbusExceptionCode.IllegalDataAddress;
            }
        }
        return 0;
    }

    public byte WriteRegisters(byte unitId, ushort start, ReadOnlySpan<ushort> values, ModbusRequestContext context)
    {
        var unit = Resolve(unitId);
        if (unit is null) return ModbusExceptionCode.GatewayPathUnavailable;

        lock (unit.Gate)
        {
            var blocks = unit.Blocks(ModbusArea.HoldingRegister);
            var end = start + values.Length;
            var touched = blocks.Where(b => b.WireStart < end && b.WireEnd > start).ToList();
            if (touched.Count == 0) return ModbusExceptionCode.IllegalDataAddress;

            var applied = 0;
            foreach (var block in touched)
                applied += block.ApplyWrite(start, values, ReadOnlySpan<bool>.Empty, _writerId, _bus);

            if (applied == 0)
            {
                Log.Debug($"server:{_config.Name}",
                          $"{context.RemoteEndPoint} wrote registers {start}..{end - 1} but no point there is writable.");
                return ModbusExceptionCode.IllegalDataAddress;
            }
        }
        return 0;
    }

    public byte MaskWriteRegister(byte unitId, ushort address, ushort andMask, ushort orMask,
                                  ModbusRequestContext context)
    {
        var unit = Resolve(unitId);
        if (unit is null) return ModbusExceptionCode.GatewayPathUnavailable;

        lock (unit.Gate)
        {
            var block = unit.Blocks(ModbusArea.HoldingRegister).FirstOrDefault(b => b.Contains(address));
            if (block is null) return ModbusExceptionCode.IllegalDataAddress;

            block.Refresh();
            var current = block.Registers[address - block.WireStart];
            var updated = (ushort)(current & andMask | orMask & ~andMask);

            Span<ushort> one = stackalloc ushort[1];
            one[0] = updated;
            var applied = block.ApplyWrite(address, one, ReadOnlySpan<bool>.Empty, _writerId, _bus);
            if (applied == 0) return ModbusExceptionCode.IllegalDataAddress;
        }
        return 0;
    }
}
