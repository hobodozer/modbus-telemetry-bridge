using System.Buffers.Binary;
using System.Net.Sockets;
using ModbusBridge.Core.Diagnostics;

namespace ModbusBridge.Core.Modbus;

/// <summary>
/// A minimal, allocation-light Modbus TCP master. One transaction is in flight at a time, which is
/// what almost every PLC stack expects; concurrency comes from running several clients in parallel.
/// </summary>
public sealed class ModbusTcpClient : IDisposable
{
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private readonly byte[] _requestBuffer = new byte[ModbusLimits.MaxAduLength];
    private readonly byte[] _responseBuffer = new byte[ModbusLimits.MaxAduLength];

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _transactionId;

    public ModbusTcpClient(string host, int port)
    {
        Host = host;
        Port = port;
    }

    public string Host { get; }
    public int Port { get; }

    public int ConnectTimeoutMs { get; set; } = 3000;
    public int ResponseTimeoutMs { get; set; } = 1000;

    public bool IsConnected => _tcp?.Connected == true && _stream is not null;

    /// <summary>Round-trip time of the last completed transaction.</summary>
    public double LastRoundTripMs { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        Close();

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeoutMs);
            await tcp.ConnectAsync(Host, Port, timeout.Token).ConfigureAwait(false);

            _tcp = tcp;
            _stream = tcp.GetStream();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"Connection to {Host}:{Port} timed out after {ConnectTimeoutMs} ms.");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public void Close()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
    }

    // ---- Read functions -------------------------------------------------------------------

    public async Task<bool[]> ReadCoilsAsync(byte unitId, ushort start, ushort count, CancellationToken ct) =>
        await ReadBitsAsync(FunctionCode.ReadCoils, unitId, start, count, ct).ConfigureAwait(false);

    public async Task<bool[]> ReadDiscreteInputsAsync(byte unitId, ushort start, ushort count, CancellationToken ct) =>
        await ReadBitsAsync(FunctionCode.ReadDiscreteInputs, unitId, start, count, ct).ConfigureAwait(false);

    public async Task<ushort[]> ReadHoldingRegistersAsync(byte unitId, ushort start, ushort count, CancellationToken ct) =>
        await ReadRegistersAsync(FunctionCode.ReadHoldingRegisters, unitId, start, count, ct).ConfigureAwait(false);

    public async Task<ushort[]> ReadInputRegistersAsync(byte unitId, ushort start, ushort count, CancellationToken ct) =>
        await ReadRegistersAsync(FunctionCode.ReadInputRegisters, unitId, start, count, ct).ConfigureAwait(false);

    private async Task<bool[]> ReadBitsAsync(byte function, byte unitId, ushort start, ushort count, CancellationToken ct)
    {
        if (count is < 1 or > ModbusLimits.MaxReadCoils)
            throw new ArgumentOutOfRangeException(nameof(count), $"Bit count must be 1..{ModbusLimits.MaxReadCoils}.");

        var pdu = new byte[5];
        pdu[0] = function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), count);

        var response = await TransactAsync(unitId, pdu, ct).ConfigureAwait(false);

        var expectedBytes = (count + 7) / 8;
        // Both halves matter: a device that declares the right byte count but sends a short PDU
        // would otherwise be indexed past the end of the response.
        if (response.Length < 2 + expectedBytes || response[1] != expectedBytes)
            throw new InvalidDataException($"Malformed response to function {function:X2}: expected {expectedBytes} data byte(s).");

        var result = new bool[count];
        for (var i = 0; i < count; i++)
            result[i] = (response[2 + i / 8] >> i % 8 & 1) != 0;
        return result;
    }

    private async Task<ushort[]> ReadRegistersAsync(byte function, byte unitId, ushort start, ushort count, CancellationToken ct)
    {
        if (count is < 1 or > ModbusLimits.MaxReadRegisters)
            throw new ArgumentOutOfRangeException(nameof(count), $"Register count must be 1..{ModbusLimits.MaxReadRegisters}.");

        var pdu = new byte[5];
        pdu[0] = function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), count);

        var response = await TransactAsync(unitId, pdu, ct).ConfigureAwait(false);

        if (response.Length < 2 + count * 2 || response[1] != count * 2)
            throw new InvalidDataException($"Malformed response to function {function:X2}: expected {count * 2} data byte(s).");

        var result = new ushort[count];
        for (var i = 0; i < count; i++)
            result[i] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2 + i * 2));
        return result;
    }

    // ---- Write functions ------------------------------------------------------------------

    public async Task WriteSingleCoilAsync(byte unitId, ushort address, bool value, CancellationToken ct)
    {
        var pdu = new byte[5];
        pdu[0] = FunctionCode.WriteSingleCoil;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value ? (ushort)0xFF00 : (ushort)0x0000);
        await TransactAsync(unitId, pdu, ct).ConfigureAwait(false);
    }

    public async Task WriteSingleRegisterAsync(byte unitId, ushort address, ushort value, CancellationToken ct)
    {
        var pdu = new byte[5];
        pdu[0] = FunctionCode.WriteSingleRegister;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value);
        await TransactAsync(unitId, pdu, ct).ConfigureAwait(false);
    }

    public async Task WriteMultipleCoilsAsync(byte unitId, ushort start, bool[] values, CancellationToken ct)
    {
        if (values.Length is < 1 or > ModbusLimits.MaxWriteCoils)
            throw new ArgumentOutOfRangeException(nameof(values), $"Coil count must be 1..{ModbusLimits.MaxWriteCoils}.");

        var dataBytes = (values.Length + 7) / 8;
        var pdu = new byte[6 + dataBytes];
        pdu[0] = FunctionCode.WriteMultipleCoils;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Length);
        pdu[5] = (byte)dataBytes;
        for (var i = 0; i < values.Length; i++)
            if (values[i]) pdu[6 + i / 8] |= (byte)(1 << i % 8);

        await TransactAsync(unitId, pdu, ct).ConfigureAwait(false);
    }

    public async Task WriteMultipleRegistersAsync(byte unitId, ushort start, ushort[] values, CancellationToken ct)
    {
        if (values.Length is < 1 or > ModbusLimits.MaxWriteRegisters)
            throw new ArgumentOutOfRangeException(nameof(values), $"Register count must be 1..{ModbusLimits.MaxWriteRegisters}.");

        var pdu = new byte[6 + values.Length * 2];
        pdu[0] = FunctionCode.WriteMultipleRegisters;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Length);
        pdu[5] = (byte)(values.Length * 2);
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + i * 2), values[i]);

        await TransactAsync(unitId, pdu, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// FC22 - atomically sets and clears bits in one register: <c>new = (current AND and) OR (or AND NOT and)</c>.
    /// Useful for touching one packed bit without a read-modify-write race against the PLC program.
    /// </summary>
    public async Task MaskWriteRegisterAsync(byte unitId, ushort address, ushort andMask, ushort orMask, CancellationToken ct)
    {
        var pdu = new byte[7];
        pdu[0] = FunctionCode.MaskWriteRegister;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), andMask);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(5), orMask);
        await TransactAsync(unitId, pdu, ct).ConfigureAwait(false);
    }

    // ---- Transport ------------------------------------------------------------------------

    /// <summary>Sends a PDU and returns the response PDU (function code included, exceptions thrown).</summary>
    public async Task<byte[]> TransactAsync(byte unitId, byte[] requestPdu, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected.");

        await _transactionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var transactionId = unchecked(++_transactionId);
            var length = requestPdu.Length + 1; // unit id + PDU

            BinaryPrimitives.WriteUInt16BigEndian(_requestBuffer.AsSpan(0), transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(_requestBuffer.AsSpan(2), 0); // protocol id
            BinaryPrimitives.WriteUInt16BigEndian(_requestBuffer.AsSpan(4), (ushort)length);
            _requestBuffer[6] = unitId;
            requestPdu.CopyTo(_requestBuffer.AsSpan(7));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ResponseTimeoutMs);
            var token = timeout.Token;

            var startTicks = Clock.Ticks;

            try
            {
                await stream.WriteAsync(_requestBuffer.AsMemory(0, 7 + requestPdu.Length), token).ConfigureAwait(false);

                // Responses to an earlier, timed-out request may still be queued; skip them.
                while (true)
                {
                    await stream.ReadExactlyAsync(_responseBuffer.AsMemory(0, 7), token).ConfigureAwait(false);

                    var responseTransactionId = BinaryPrimitives.ReadUInt16BigEndian(_responseBuffer.AsSpan(0));
                    var protocolId = BinaryPrimitives.ReadUInt16BigEndian(_responseBuffer.AsSpan(2));
                    var responseLength = BinaryPrimitives.ReadUInt16BigEndian(_responseBuffer.AsSpan(4));

                    if (protocolId != 0)
                        throw new InvalidDataException($"Unexpected protocol id {protocolId} in response.");
                    if (responseLength is < 2 or > ModbusLimits.MaxPduLength + 1)
                        throw new InvalidDataException($"Response length {responseLength} is out of range.");

                    var pduLength = responseLength - 1;
                    await stream.ReadExactlyAsync(_responseBuffer.AsMemory(7, pduLength), token).ConfigureAwait(false);

                    if (responseTransactionId != transactionId) continue; // stale reply, keep reading

                    LastRoundTripMs = Clock.MsSince(startTicks);

                    var function = _responseBuffer[7];
                    if ((function & FunctionCode.ExceptionFlag) != 0)
                    {
                        var exceptionCode = pduLength >= 2 ? _responseBuffer[8] : (byte)0;
                        throw new ModbusProtocolException((byte)(function & 0x7F), exceptionCode);
                    }

                    if (function != requestPdu[0])
                        throw new InvalidDataException($"Response function {function:X2} does not match request {requestPdu[0]:X2}.");

                    return _responseBuffer.AsSpan(7, pduLength).ToArray();
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // A timed-out transaction leaves the stream out of sync; force a reconnect.
                Close();
                throw new TimeoutException($"No response from {Host}:{Port} within {ResponseTimeoutMs} ms.");
            }
            catch (EndOfStreamException)
            {
                Close();
                throw new IOException($"{Host}:{Port} closed the connection.");
            }
        }
        finally
        {
            _transactionGate.Release();
        }
    }

    public void Dispose()
    {
        Close();
        _transactionGate.Dispose();
    }
}
