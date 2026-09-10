namespace ModbusBridge.Core.Modbus;

/// <summary>Identifies the peer that issued a request, for logging and write attribution.</summary>
public sealed record ModbusRequestContext(string ClientId, string RemoteEndPoint);

/// <summary>
/// Backing store a <see cref="ModbusTcpServer"/> serves from. Every method returns 0 on success or
/// a Modbus exception code (see <see cref="ModbusExceptionCode"/>) to be returned to the client.
/// Implementations must be thread-safe: several client connections call in concurrently.
/// </summary>
public interface IModbusDataStore
{
    /// <summary>True when this store has a map for the unit id (drives exception 0x0A).</summary>
    bool HasUnit(byte unitId);

    byte ReadCoils(byte unitId, ushort start, ushort count, Span<bool> destination);
    byte ReadDiscreteInputs(byte unitId, ushort start, ushort count, Span<bool> destination);
    byte ReadHoldingRegisters(byte unitId, ushort start, ushort count, Span<ushort> destination);
    byte ReadInputRegisters(byte unitId, ushort start, ushort count, Span<ushort> destination);

    byte WriteCoils(byte unitId, ushort start, ReadOnlySpan<bool> values, ModbusRequestContext context);
    byte WriteRegisters(byte unitId, ushort start, ReadOnlySpan<ushort> values, ModbusRequestContext context);

    /// <summary>Read-modify-write of one holding register for FC22.</summary>
    byte MaskWriteRegister(byte unitId, ushort address, ushort andMask, ushort orMask, ModbusRequestContext context);
}

/// <summary>Strings returned for FC43/MEI 14 (Read Device Identification), used by discovery tools.</summary>
public sealed class DeviceIdentity
{
    public string VendorName { get; set; } = "ModbusBridge";
    public string ProductCode { get; set; } = "MBBRIDGE";
    public string MajorMinorRevision { get; set; } = "1.0";
    public string VendorUrl { get; set; } = "";
    public string ProductName { get; set; } = "Modbus Telemetry Bridge";
    public string ModelName { get; set; } = "";
    public string UserApplicationName { get; set; } = "";
}
