using ModbusBridge.Core.Modbus;

namespace ModbusBridge.Core.Simulation;

/// <summary>
/// A plain RAM-backed Modbus slave used to stand in for a PLC on the bench. Coils and discrete
/// inputs animate slowly and input registers ramp, so a client device entry pointed at it shows
/// live data without any hardware.
/// </summary>
public sealed class VirtualPlcStore : IModbusDataStore
{
    private const int Size = 2048;

    private readonly bool[] _coils = new bool[Size];
    private readonly bool[] _discreteInputs = new bool[Size];
    private readonly ushort[] _holdingRegisters = new ushort[Size];
    private readonly ushort[] _inputRegisters = new ushort[Size];
    private readonly object _gate = new();
    private readonly long _startTicks = Environment.TickCount64;

    /// <summary>Answer for every unit id, so a misconfigured client still gets data on the bench.</summary>
    public bool HasUnit(byte unitId) => true;

    /// <summary>Advances the synthetic inputs. Call at roughly 20 Hz.</summary>
    public void Animate()
    {
        var seconds = (Environment.TickCount64 - _startTicks) / 1000.0;

        lock (_gate)
        {
            // A slow-marching pattern of contacts, so every bit eventually toggles.
            for (var i = 0; i < 64; i++)
            {
                var phase = seconds / (1.0 + i * 0.05);
                _discreteInputs[i] = phase % 2.0 < 1.0;
                _coils[i] = (phase + 0.5) % 2.0 < 1.0;
            }

            // Full-scale Siemens-style analog counts on the first few registers.
            for (var i = 0; i < 8; i++)
            {
                var value = (Math.Sin(seconds * 0.5 + i * 0.7) + 1.0) * 0.5 * 27648.0;
                _inputRegisters[i] = (ushort)(short)Math.Round(value);
                _holdingRegisters[i] = _inputRegisters[i];
            }
        }
    }

    private static byte Read<T>(T[] source, ushort start, ushort count, Span<T> destination)
    {
        if (start + count > source.Length) return ModbusExceptionCode.IllegalDataAddress;
        source.AsSpan(start, count).CopyTo(destination);
        return 0;
    }

    public byte ReadCoils(byte unitId, ushort start, ushort count, Span<bool> destination)
    {
        lock (_gate) return Read(_coils, start, count, destination);
    }

    public byte ReadDiscreteInputs(byte unitId, ushort start, ushort count, Span<bool> destination)
    {
        lock (_gate) return Read(_discreteInputs, start, count, destination);
    }

    public byte ReadHoldingRegisters(byte unitId, ushort start, ushort count, Span<ushort> destination)
    {
        lock (_gate) return Read(_holdingRegisters, start, count, destination);
    }

    public byte ReadInputRegisters(byte unitId, ushort start, ushort count, Span<ushort> destination)
    {
        lock (_gate) return Read(_inputRegisters, start, count, destination);
    }

    public byte WriteCoils(byte unitId, ushort start, ReadOnlySpan<bool> values, ModbusRequestContext context)
    {
        if (start + values.Length > Size) return ModbusExceptionCode.IllegalDataAddress;
        lock (_gate) values.CopyTo(_coils.AsSpan(start));
        return 0;
    }

    public byte WriteRegisters(byte unitId, ushort start, ReadOnlySpan<ushort> values, ModbusRequestContext context)
    {
        if (start + values.Length > Size) return ModbusExceptionCode.IllegalDataAddress;
        lock (_gate) values.CopyTo(_holdingRegisters.AsSpan(start));
        return 0;
    }

    public byte MaskWriteRegister(byte unitId, ushort address, ushort andMask, ushort orMask,
                                  ModbusRequestContext context)
    {
        if (address >= Size) return ModbusExceptionCode.IllegalDataAddress;
        lock (_gate)
        {
            var current = _holdingRegisters[address];
            _holdingRegisters[address] = (ushort)(current & andMask | orMask & ~andMask);
        }
        return 0;
    }

    /// <summary>Lets the UI show what the virtual PLC currently holds.</summary>
    public ushort PeekHoldingRegister(int address)
    {
        lock (_gate) return address >= 0 && address < Size ? _holdingRegisters[address] : (ushort)0;
    }
}
