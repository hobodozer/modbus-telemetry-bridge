using System.Collections.ObjectModel;
using ModbusBridge.Core.Data;

namespace ModbusBridge.Core.Config;

/// <summary>A contiguous block of registers/bits polled from a PLC on one schedule.</summary>
public sealed class ReadGroupConfig
{
    public string Name { get; set; } = "read group";
    public bool Enabled { get; set; } = true;
    public ModbusArea Area { get; set; } = ModbusArea.HoldingRegister;

    /// <summary>Start address, expressed in the device's configured address base.</summary>
    public int StartAddress { get; set; }

    /// <summary>Registers (or bits) to read. Split automatically to respect protocol limits.</summary>
    public int Count { get; set; } = 1;

    public int PollIntervalMs { get; set; } = 100;

    /// <summary>Optional unit id override for gateways fronting several devices.</summary>
    public byte? UnitId { get; set; }

    public ObservableCollection<PointConfig> Points { get; set; } = new();
}

/// <summary>A block of registers/bits written to a PLC from tag values.</summary>
public sealed class WriteGroupConfig
{
    public string Name { get; set; } = "write group";
    public bool Enabled { get; set; } = true;
    public ModbusArea Area { get; set; } = ModbusArea.HoldingRegister;
    public int StartAddress { get; set; }

    /// <summary>Registers/bits spanned. 0 means "derive from the points".</summary>
    public int Count { get; set; }

    public WriteMode Mode { get; set; } = WriteMode.OnChange;

    /// <summary>Interval for Periodic / OnChangeAndPeriodic, and the minimum gap between on-change writes.</summary>
    public int PeriodMs { get; set; } = 100;

    /// <summary>Use FC15/FC16 (multi-write) rather than FC5/FC6 even for a single point.</summary>
    public bool UseMultipleWrite { get; set; } = true;

    /// <summary>
    /// Write the whole block every cycle instead of only the changed span. Slower but required by
    /// PLCs that latch on a full-block write.
    /// </summary>
    public bool AlwaysWriteWholeBlock { get; set; }

    public byte? UnitId { get; set; }

    public ObservableCollection<PointConfig> Points { get; set; } = new();
}

/// <summary>
/// Heartbeat exchanged with a PLC so both sides can detect a dead link. We increment
/// <see cref="WriteAddress"/> every <see cref="IntervalMs"/>; if the PLC mirrors it back on
/// <see cref="ReadAddress"/> and that value stops changing, the link is declared dead.
/// </summary>
public sealed class WatchdogConfig
{
    public bool Enabled { get; set; }
    public ModbusArea WriteArea { get; set; } = ModbusArea.HoldingRegister;
    public int WriteAddress { get; set; }
    public int IntervalMs { get; set; } = 500;

    /// <summary>Set to -1 to send a heartbeat without expecting one back.</summary>
    public int ReadAddress { get; set; } = -1;
    public ModbusArea ReadArea { get; set; } = ModbusArea.HoldingRegister;

    /// <summary>Declare the link dead after this long without the echo changing.</summary>
    public int TimeoutMs { get; set; } = 2000;

    /// <summary>Optional tag that mirrors the link state (1 = healthy).</summary>
    public string? HealthTag { get; set; }
}

/// <summary>A connection to a PLC (or any device) that hosts a Modbus TCP server.</summary>
public sealed class ModbusClientConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "PLC";
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = "192.168.1.10";
    public int Port { get; set; } = 502;
    public byte UnitId { get; set; } = 1;

    public int ConnectTimeoutMs { get; set; } = 3000;
    public int ResponseTimeoutMs { get; set; } = 1000;

    /// <summary>Reconnect backoff, doubling from Initial up to Max.</summary>
    public int ReconnectDelayMs { get; set; } = 1000;
    public int MaxReconnectDelayMs { get; set; } = 15000;

    /// <summary>Consecutive failures before the device is declared offline and tags go bad.</summary>
    public int FailuresBeforeOffline { get; set; } = 3;

    /// <summary>
    /// Subtracted from every configured address to get the wire address. 0 for wire addresses,
    /// 1 for vendor docs that number from 1. Overridden per area by <see cref="AddressBaseByArea"/>.
    /// </summary>
    public int AddressBase { get; set; }

    /// <summary>
    /// Per-area address base, for Modicon 4xxxx-style notation where each area has its own offset.
    /// Null falls back to <see cref="AddressBase"/> for every area.
    /// </summary>
    public Dictionary<ModbusArea, int>? AddressBaseByArea { get; set; }

    public int BaseFor(ModbusArea area) =>
        AddressBaseByArea is not null && AddressBaseByArea.TryGetValue(area, out var value)
            ? value
            : AddressBase;

    public WordOrder WordOrder { get; set; } = WordOrder.HighFirst;
    public ByteOrder ByteOrder { get; set; } = ByteOrder.HighFirst;

    /// <summary>Protocol caps; lower them for devices with small buffers.</summary>
    public int MaxRegistersPerRead { get; set; } = 125;
    public int MaxCoilsPerRead { get; set; } = 2000;
    public int MaxRegistersPerWrite { get; set; } = 123;
    public int MaxCoilsPerWrite { get; set; } = 1968;

    /// <summary>Pause inserted between requests for devices that cannot keep up.</summary>
    public int InterRequestDelayMs { get; set; }

    /// <summary>Prefix applied to every point's tag name in this device, e.g. "plc1.".</summary>
    public string TagPrefix { get; set; } = "";

    public WatchdogConfig Watchdog { get; set; } = new();

    public ObservableCollection<ReadGroupConfig> ReadGroups { get; set; } = new();
    public ObservableCollection<WriteGroupConfig> WriteGroups { get; set; } = new();
}

