namespace ModbusBridge.Core.Data;

/// <summary>Modbus data area a point lives in.</summary>
public enum ModbusArea
{
    /// <summary>Read/write bits (FC 1 / 5 / 15).</summary>
    Coil,
    /// <summary>Read-only bits (FC 2).</summary>
    DiscreteInput,
    /// <summary>Read/write 16-bit words (FC 3 / 6 / 16).</summary>
    HoldingRegister,
    /// <summary>Read-only 16-bit words (FC 4).</summary>
    InputRegister
}

/// <summary>Value encoding of a point across one or more registers.</summary>
public enum PointDataType
{
    Bool,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float32,
    Float64,
    /// <summary>ASCII packed two chars per register; <see cref="Config.PointConfig.Length"/> gives the register count.</summary>
    String
}

/// <summary>Which register carries the most significant word of a multi-register value.</summary>
public enum WordOrder
{
    /// <summary>High word first (regs[0] is most significant). Most common.</summary>
    HighFirst,
    /// <summary>Low word first (regs[0] is least significant). Common on AB / some CODESYS stacks.</summary>
    LowFirst
}

/// <summary>Byte order within a single 16-bit register.</summary>
public enum ByteOrder
{
    /// <summary>High byte first - the Modbus wire standard.</summary>
    HighFirst,
    /// <summary>Bytes swapped inside each register.</summary>
    Swapped
}

/// <summary>Data quality, in the OPC sense.</summary>
public enum TagQuality
{
    /// <summary>Never written since startup.</summary>
    Never = 0,
    /// <summary>Source is offline or returned an error.</summary>
    Bad = 1,
    /// <summary>Last known value, but older than the configured stale timeout.</summary>
    Stale = 2,
    /// <summary>Fresh and trustworthy.</summary>
    Good = 3
}

/// <summary>What a Modbus client (HMI) is allowed to do with a point we serve.</summary>
public enum AccessMode
{
    Read,
    Write,
    ReadWrite
}

/// <summary>When a write group pushes values to a PLC.</summary>
public enum WriteMode
{
    /// <summary>Only when a mapped tag's value changes (beyond its deadband).</summary>
    OnChange,
    /// <summary>On a fixed interval regardless of change.</summary>
    Periodic,
    /// <summary>On change, plus a periodic refresh as a keepalive.</summary>
    OnChangeAndPeriodic
}

/// <summary>What the server reports for a point whose tag has gone bad or stale.</summary>
public enum StaleBehavior
{
    /// <summary>Keep serving the last known value.</summary>
    HoldLastValue,
    /// <summary>Serve the point's configured failsafe value.</summary>
    Failsafe,
    /// <summary>Return Modbus exception 4 (slave device failure) for any read touching the point.</summary>
    ModbusException
}

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4
}
