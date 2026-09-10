using ModbusBridge.Core.Config;
using ModbusBridge.Core.Data;
using ModbusBridge.Core.Tags;

namespace ModbusBridge.Core.Engine;

/// <summary>
/// A <see cref="PointConfig"/> resolved against the tag bus and reduced to wire addressing, so the
/// hot path never touches config lookups or string comparisons.
/// </summary>
public sealed class BoundPoint
{
    public required PointConfig Config { get; init; }
    public required TagEntry Tag { get; init; }

    /// <summary>Zero-based wire address of the point's first register or bit.</summary>
    public required int WireAddress { get; init; }

    public required int Size { get; init; }
    public required WordOrder WordOrder { get; init; }
    public required ByteOrder ByteOrder { get; init; }

    public int WireEnd => WireAddress + Size;

    public PointDataType DataType => Config.DataType;
    public int BitIndex => Config.BitIndex;

    /// <summary>Tag version the image was last built from; -1 forces a rebuild.</summary>
    public long LastVersion { get; set; } = -1;

    /// <summary>Quality the image was last built with, so a Good-to-Stale flip forces a re-encode.</summary>
    public TagQuality LastQuality { get; set; } = TagQuality.Never;

    /// <summary>Last engineering value pushed to a device, for deadband comparison.</summary>
    public double LastWrittenValue { get; set; } = double.NaN;

    public bool IsWritable => Config.Access is AccessMode.Write or AccessMode.ReadWrite;

    /// <summary>Resolves the engineering value to publish, applying stale/failsafe policy.</summary>
    public double EffectiveValue(StaleBehavior behavior, int staleTimeoutMs, out bool failed)
    {
        var value = Tag.Value;
        var bad = value.Quality is TagQuality.Never or TagQuality.Bad;

        if (!bad && staleTimeoutMs > 0 &&
            (DateTime.UtcNow - value.TimestampUtc).TotalMilliseconds > staleTimeoutMs)
            bad = true;
        else if (!bad && value.Quality == TagQuality.Stale)
            bad = true;

        if (!bad)
        {
            failed = false;
            return value.Number;
        }

        switch (behavior)
        {
            case StaleBehavior.Failsafe:
                failed = false;
                return Config.FailsafeValue;
            case StaleBehavior.ModbusException:
                failed = true;
                return value.Number;
            default:
                failed = false;
                return value.Number;
        }
    }
}

