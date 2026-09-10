using ModbusBridge.Core.Data;

namespace ModbusBridge.Core.Config;

/// <summary>
/// Linear conversion between the raw wire value and the engineering value held in the tag.
/// Applied as <c>eng = raw * Gain + Offset</c> on read and inverted on write.
/// </summary>
public sealed class ScalingConfig
{
    public double Gain { get; set; } = 1.0;
    public double Offset { get; set; }

    /// <summary>Clamp applied to the engineering value. Null disables that side.</summary>
    public double? Min { get; set; }
    public double? Max { get; set; }

    /// <summary>
    /// Change smaller than this (in engineering units) is not treated as a change by
    /// on-change write groups. 0 disables.
    /// </summary>
    public double Deadband { get; set; }

    /// <summary>Decimal places used for display only.</summary>
    public int? DisplayDecimals { get; set; }

    public bool IsIdentity => Gain == 1.0 && Offset == 0.0 && Min is null && Max is null;

    public double ToEngineering(double raw) => Clamp(raw * Gain + Offset);

    public double ToRaw(double engineering)
    {
        var value = Clamp(engineering);
        return Gain == 0.0 ? 0.0 : (value - Offset) / Gain;
    }

    private double Clamp(double value)
    {
        if (Min is { } min && value < min) value = min;
        if (Max is { } max && value > max) value = max;
        return value;
    }

    public ScalingConfig Clone() => (ScalingConfig)MemberwiseClone();
}

/// <summary>
/// One mapped point: a tag bound to an address within a read group, write group or server block.
/// </summary>
public sealed class PointConfig
{
    /// <summary>Tag name in the bus. Auto-created if it does not exist.</summary>
    public string Tag { get; set; } = "";

    /// <summary>Register or bit offset relative to the parent group's start address.</summary>
    public int Offset { get; set; }

    public PointDataType DataType { get; set; } = PointDataType.UInt16;

    /// <summary>
    /// For <see cref="PointDataType.Bool"/> inside a register area: which bit (0-15) carries the
    /// value. -1 means "not a packed bit" - in a coil area the offset already addresses a bit.
    /// </summary>
    public int BitIndex { get; set; } = -1;

    /// <summary>Register count for <see cref="PointDataType.String"/> (2 ASCII chars per register).</summary>
    public int Length { get; set; }

    /// <summary>
    /// Inverts the point in software. For a <see cref="PointDataType.Bool"/> this is the
    /// normally-open / normally-closed toggle: with it set, a closed contact reads as 0 and an open
    /// contact reads as 1. For a numeric it negates the engineering value after scaling.
    /// Applies symmetrically on writes, so a point round-trips correctly.
    /// </summary>
    public bool Invert { get; set; }

    public ScalingConfig? Scale { get; set; }

    /// <summary>Server-side only: what a connected Modbus client may do with this point.</summary>
    public AccessMode Access { get; set; } = AccessMode.Read;

    /// <summary>Per-point override of the device word order. Null inherits the device setting.</summary>
    public WordOrder? WordOrder { get; set; }

    /// <summary>Per-point override of the device byte order. Null inherits the device setting.</summary>
    public ByteOrder? ByteOrder { get; set; }

    /// <summary>Value substituted when the tag is bad/stale and the block uses Failsafe behaviour.</summary>
    public double FailsafeValue { get; set; }

    public bool Enabled { get; set; } = true;

    public string? Description { get; set; }

    public string? Units { get; set; }

    /// <summary>How many registers (or bits) this point occupies.</summary>
    public int Size => DataType switch
    {
        PointDataType.String => Math.Max(1, Length),
        PointDataType.Bool => 1,
        PointDataType.Int16 or PointDataType.UInt16 => 1,
        PointDataType.Int32 or PointDataType.UInt32 or PointDataType.Float32 => 2,
        PointDataType.Int64 or PointDataType.UInt64 or PointDataType.Float64 => 4,
        _ => 1
    };

    /// <summary>Converts a decoded wire value into the engineering value stored in the tag.</summary>
    public double RawToEngineering(double raw)
    {
        if (DataType == PointDataType.Bool)
        {
            var state = raw != 0d;
            return state ^ Invert ? 1d : 0d;
        }

        var value = Scale?.ToEngineering(raw) ?? raw;
        return Invert ? -value : value;
    }

    /// <summary>Converts an engineering value from the tag back into the value to put on the wire.</summary>
    public double EngineeringToRaw(double engineering)
    {
        if (DataType == PointDataType.Bool)
        {
            var state = engineering != 0d;
            return state ^ Invert ? 1d : 0d;
        }

        var value = Invert ? -engineering : engineering;
        return Scale?.ToRaw(value) ?? value;
    }

    /// <summary>Deadband in engineering units, used by on-change writes and change detection.</summary>
    public double Deadband => Scale?.Deadband ?? 0d;

    public PointConfig Clone()
    {
        var copy = (PointConfig)MemberwiseClone();
        copy.Scale = Scale?.Clone();
        return copy;
    }
}
