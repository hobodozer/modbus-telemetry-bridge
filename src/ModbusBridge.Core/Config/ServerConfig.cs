using System.Collections.ObjectModel;
using ModbusBridge.Core.Data;

namespace ModbusBridge.Core.Config;

/// <summary>A contiguous span of one Modbus area that we expose to connecting clients.</summary>
public sealed class ServerBlockConfig
{
    public string Name { get; set; } = "block";
    public bool Enabled { get; set; } = true;
    public ModbusArea Area { get; set; } = ModbusArea.HoldingRegister;

    /// <summary>Start address in the server's configured address base.</summary>
    public int StartAddress { get; set; }

    /// <summary>Registers/bits reserved. Addresses inside the block but unmapped read as 0.</summary>
    public int Size { get; set; } = 16;

    public StaleBehavior StaleBehavior { get; set; } = StaleBehavior.HoldLastValue;

    /// <summary>Tags older than this are treated as stale by <see cref="StaleBehavior"/>. 0 disables.</summary>
    public int StaleTimeoutMs { get; set; }

    public ObservableCollection<PointConfig> Points { get; set; } = new();
}

/// <summary>
/// The register map presented under one unit id. Two HMIs can hit the same listener with
/// different unit ids and see entirely different maps.
/// </summary>
public sealed class ServerMapConfig
{
    public string Name { get; set; } = "map";
    public bool Enabled { get; set; } = true;
    public byte UnitId { get; set; } = 1;

    /// <summary>Also answer requests addressed to unit id 0 (broadcast) and 255 (ignore-unit).</summary>
    public bool AcceptAnyUnitId { get; set; } = true;

    public string TagPrefix { get; set; } = "";

    public ObservableCollection<ServerBlockConfig> Blocks { get; set; } = new();
}

/// <summary>A Modbus TCP listener that HMIs and other clients connect to.</summary>
public sealed class ModbusServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "HMI server";
    public bool Enabled { get; set; } = true;

    /// <summary>0.0.0.0 for every interface, or a specific NIC address.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 502;

    public int MaxClients { get; set; } = 16;

    /// <summary>Empty = allow anyone. Entries may be a plain IP or CIDR, e.g. "192.168.1.0/24".</summary>
    public ObservableCollection<string> AllowedClients { get; set; } = new();

    /// <summary>Reject every write function code regardless of per-point access.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Drop a client that has sent nothing for this long. 0 disables.</summary>
    public int IdleTimeoutSec { get; set; } = 120;

    /// <summary>Disable Nagle so small HMI polls are not delayed ~40 ms.</summary>
    public bool NoDelay { get; set; } = true;

    /// <summary>Subtracted from configured addresses to get wire addresses. See <see cref="Addressing"/>.</summary>
    public int AddressBase { get; set; }

    /// <summary>Per-area override of <see cref="AddressBase"/>, for Modicon-style notation.</summary>
    public Dictionary<ModbusArea, int>? AddressBaseByArea { get; set; }

    public int BaseFor(ModbusArea area) =>
        AddressBaseByArea is not null && AddressBaseByArea.TryGetValue(area, out var value)
            ? value
            : AddressBase;

    public WordOrder WordOrder { get; set; } = WordOrder.HighFirst;
    public ByteOrder ByteOrder { get; set; } = ByteOrder.HighFirst;

    public ObservableCollection<ServerMapConfig> Maps { get; set; } = new();
}

