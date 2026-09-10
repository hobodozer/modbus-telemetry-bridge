using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ModbusBridge.Core.Diagnostics;

namespace ModbusBridge.Core.Modbus;

/// <summary>Live state of one connected Modbus client, surfaced in the UI.</summary>
public sealed class ModbusClientSession
{
    public required string Id { get; init; }
    public required string RemoteEndPoint { get; init; }
    public DateTime ConnectedAtLocal { get; init; } = DateTime.Now;
    public DateTime LastRequestLocal { get; set; } = DateTime.Now;

    private long _requests;
    private long _errors;

    public long Requests => Interlocked.Read(ref _requests);
    public long Errors => Interlocked.Read(ref _errors);
    public byte LastFunctionCode { get; set; }
    public byte LastUnitId { get; set; }

    internal void CountRequest() => Interlocked.Increment(ref _requests);
    internal void CountError() => Interlocked.Increment(ref _errors);
}

/// <summary>
/// A multi-client Modbus TCP listener. Each accepted connection gets its own task; all of them read
/// and write through one <see cref="IModbusDataStore"/>, so any number of HMIs see a consistent map.
/// </summary>
public sealed class ModbusTcpServer : IAsyncDisposable
{
    private readonly IModbusDataStore _store;
    private readonly ConcurrentDictionary<string, ModbusClientSession> _sessions = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private int _clientCount;

    public ModbusTcpServer(string name, IModbusDataStore store)
    {
        Name = name;
        _store = store;
    }

    public string Name { get; }
    public IPAddress BindAddress { get; set; } = IPAddress.Any;
    public int Port { get; set; } = 502;
    public int MaxClients { get; set; } = 16;
    public bool ReadOnly { get; set; }
    public bool NoDelay { get; set; } = true;
    public int IdleTimeoutSec { get; set; } = 120;

    /// <summary>Empty means allow all. Entries are "1.2.3.4" or "1.2.3.0/24".</summary>
    public IReadOnlyList<string> AllowedClients { get; set; } = Array.Empty<string>();

    public DeviceIdentity Identity { get; set; } = new();

    public bool IsRunning => _listener is not null;
    public IReadOnlyCollection<ModbusClientSession> Sessions => _sessions.Values.ToArray();

    public event Action<ModbusClientSession>? ClientConnected;
    public event Action<ModbusClientSession>? ClientDisconnected;

    public void Start()
    {
        if (_listener is not null) return;

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(BindAddress, Port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();

        Log.Info($"server:{Name}", $"Listening on {BindAddress}:{Port} (max {MaxClients} clients" +
                                   $"{(ReadOnly ? ", read-only" : "")}).");

        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_listener is null) return;

        _cts?.Cancel();
        try { _listener.Stop(); } catch { }
        _listener = null;

        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); } catch { }
            _acceptTask = null;
        }

        _sessions.Clear();
        Interlocked.Exchange(ref _clientCount, 0);
        _cts?.Dispose();
        _cts = null;

        Log.Info($"server:{Name}", "Stopped.");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                Log.Warn($"server:{Name}", $"Accept failed: {ex.SocketErrorCode}");
                continue;
            }

            var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

            if (!IsAllowed(client.Client.RemoteEndPoint))
            {
                Log.Warn($"server:{Name}", $"Rejected {remote}: not in the allowed client list.");
                client.Dispose();
                continue;
            }

            if (Interlocked.Increment(ref _clientCount) > MaxClients)
            {
                Interlocked.Decrement(ref _clientCount);
                Log.Warn($"server:{Name}", $"Rejected {remote}: client limit of {MaxClients} reached.");
                client.Dispose();
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, remote, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, string remote, CancellationToken ct)
    {
        var session = new ModbusClientSession
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            RemoteEndPoint = remote
        };
        _sessions[session.Id] = session;
        ClientConnected?.Invoke(session);
        Log.Info($"server:{Name}", $"Client connected: {remote}");

        var request = new byte[ModbusLimits.MaxAduLength];
        var response = new byte[ModbusLimits.MaxAduLength];

        try
        {
            client.NoDelay = NoDelay;
            using var stream = client.GetStream();

            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (IdleTimeoutSec > 0) idle.CancelAfter(TimeSpan.FromSeconds(IdleTimeoutSec));

                try
                {
                    await stream.ReadExactlyAsync(request.AsMemory(0, 7), idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Log.Info($"server:{Name}", $"Client {remote} idle for {IdleTimeoutSec} s; closing.");
                    break;
                }

                var transactionId = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(0));
                var protocolId = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2));
                var length = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(4));
                var unitId = request[6];

                if (protocolId != 0 || length is < 2 or > ModbusLimits.MaxPduLength + 1)
                {
                    Log.Warn($"server:{Name}", $"Client {remote} sent a malformed MBAP header; closing.");
                    break;
                }

                var pduLength = length - 1;
                await stream.ReadExactlyAsync(request.AsMemory(7, pduLength), ct).ConfigureAwait(false);

                session.CountRequest();
                session.LastRequestLocal = DateTime.Now;
                session.LastUnitId = unitId;
                session.LastFunctionCode = request[7];

                var context = new ModbusRequestContext(session.Id, remote);
                var responsePduLength = Dispatch(unitId, request.AsSpan(7, pduLength),
                                                 response.AsSpan(7), context, session);

                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0), transactionId);
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 0);
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), (ushort)(responsePduLength + 1));
                response[6] = unitId;

                await stream.WriteAsync(response.AsMemory(0, 7 + responsePduLength), ct).ConfigureAwait(false);
            }
        }
        catch (EndOfStreamException) { /* client closed */ }
        catch (IOException) { /* client vanished */ }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            Log.Error($"server:{Name}", $"Client {remote} handler failed", ex);
        }
        finally
        {
            _sessions.TryRemove(session.Id, out _);
            Interlocked.Decrement(ref _clientCount);
            try { client.Dispose(); } catch { }
            ClientDisconnected?.Invoke(session);
            Log.Info($"server:{Name}", $"Client disconnected: {remote} ({session.Requests} requests).");
        }
    }

    /// <summary>Handles one request PDU, writing the response PDU. Returns the response length.</summary>
    private int Dispatch(byte unitId, ReadOnlySpan<byte> pdu, Span<byte> response,
                         ModbusRequestContext context, ModbusClientSession session)
    {
        var function = pdu[0];

        if (!_store.HasUnit(unitId))
            return Exception(response, function, ModbusExceptionCode.GatewayPathUnavailable, session);

        try
        {
            switch (function)
            {
                case FunctionCode.ReadCoils:
                case FunctionCode.ReadDiscreteInputs:
                {
                    if (pdu.Length < 5) return Exception(response, function, ModbusExceptionCode.IllegalDataValue, session);
                    var start = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
                    var count = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
                    if (count is < 1 or > ModbusLimits.MaxReadCoils)
                        return Exception(response, function, ModbusExceptionCode.IllegalDataValue, session);

                    Span<bool> bits = count <= 256 ? stackalloc bool[count] : new bool[count];
                    var status = function == FunctionCode.ReadCoils
                        ? _store.ReadCoils(unitId, start, count, bits)
                        : _store.ReadDiscreteInputs(unitId, start, count, bits);
                    if (status != 0) return Exception(response, function, status, session);

                    var dataBytes = (count + 7) / 8;
                    response[0] = function;
                    response[1] = (byte)dataBytes;
                    response.Slice(2, dataBytes).Clear();
                    for (var i = 0; i < count; i++)
                        if (bits[i]) response[2 + i / 8] |= (byte)(1 << i % 8);
                    return 2 + dataBytes;
                }

                case FunctionCode.ReadHoldingRegisters:
                case FunctionCode.ReadInputRegisters:
                {
                    if (pdu.Length < 5) return Exception(response, function, ModbusExceptionCode.IllegalDataValue, session);
                    var start = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
                    var count = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
                    if (count is < 1 or > ModbusLimits.MaxReadRegisters)
                        return Exception(response, function, ModbusExceptionCode.IllegalDataValue, session);

                    Span<ushort> registers = stackalloc ushort[count];
                    var status = function == FunctionCode.ReadHoldingRegisters
                        ? _store.ReadHoldingRegisters(unitId, start, count, registers)
                        : _store.ReadInputRegisters(unitId, start, count, registers);
                    if (status != 0) return Exception(response, function, status, session);

                    response[0] = function;
                    response[1] = (byte)(count * 2);
                    for (var i = 0; i < count; i++)
                        BinaryPrimitives.WriteUInt16BigEndian(response[(2 + i * 2)..], registers[i]);
                    return 2 + count * 2;
                }

                case FunctionCode.WriteSingleCoil:
                {
                    if (ReadOnly) return Exception(response, function, ModbusExceptionCode.IllegalFunction, session);
                    if (pdu.Length < 5) return Exception(response, function, ModbusExceptionCode.IllegalDataValue, session);
                    var address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
                    var raw = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
                    if (raw is not (0x0000 or 0xFF00))
                        return Exception(response, function, ModbusExceptionCode.IllegalDataValue, session);

                    Span<bool> one = stackalloc bool[1];
                    one[0] = raw == 0xFF00;
                    var status = _store.WriteCoils(unitId, address, one, context);
                    if (status != 0) return Exception(response, function, status, session);

                    pdu[..5].CopyTo(response); // echo the request
                    return 5;
                }

                case FunctionCode.WriteSingleRegister:
                {
                    if (ReadOnly) return Exception(response, function, ModbusExceptionCode.IllegalFunction, session);
                    if (pdu.Length < 5) return Exception(response, function, ModbusExceptionCode.IllegalDataValue, session);
                    var address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
                    var value = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);

                    Span<ushort> one = stackalloc ushort[1];
                    one[0] = value;
                    var status = _store.WriteRegisters(unitId, address, one, context);
                    if (status != 0) return Exception(response, function, status, session);

                    pdu[..5].CopyTo(response);
                    return 5;
                }

                case FunctionCode.WriteMultipleCoils:
                {
                    if (ReadOnly) return Exception(response, function, ModbusExceptionCode.IllegalFunction, session);
                    if (pdu.Length < 6) return Exception(response, function, ModbusExceptionCode.IllegalDataValue, session);
                    var start = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
                    var count = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
                    var byteCount = pdu[5];
                    if (count is < 1 or > ModbusLimits.MaxWriteCoils ||
                        byteCount != (count + 7) / 8 || pdu.Length < 6 + byteCount)
                        return Exception(response, function, ModbusExceptionCode.IllegalDataValue, session);

                    Span<bool> bits = count <= 256 ? stackalloc bool[count] : new bool[count];
                    for (var i = 0; i < count; i++)
                        bits[i] = (pdu[6 + i / 8] >> i % 8 & 1) != 0;

                    var status = _store.WriteCoils(unitId, start, bits, context);
                    if (status != 0) return Exception(response, function, status, session);

                    response[0] = function;
                    BinaryPrimitives.WriteUInt16BigEndian(response[1..], start);
                    BinaryPrimitives.WriteUInt16BigEndian(response[3..], count);
                    return 5;
                }

                case FunctionCode.WriteMultipleRegisters:
                {
                    if (ReadOnly) return Exception(response, function, ModbusExceptionCode.IllegalFunction, session);
                    if (pdu.Length < 6) return Exception(response, function, ModbusExceptionCode.IllegalDataValue, session);
                    var start = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
                    var count = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
                    var byteCount = pdu[5];
                    if (count is < 1 or > ModbusLimits.MaxWriteRegisters ||
                        byteCount != count * 2 || pdu.Length < 6 + byteCount)
                        return Exception(response, function, ModbusExceptionCode.IllegalDataValue, session);

                    Span<ushort> registers = stackalloc ushort[count];
                    for (var i = 0; i < count; i++)
                        registers[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu[(6 + i * 2)..]);

                    var status = _store.WriteRegisters(unitId, start, registers, context);
                    if (status != 0) return Exception(response, function, status, session);

                    response[0] = function;
                    BinaryPrimitives.WriteUInt16BigEndian(response[1..], start);
                    BinaryPrimitives.WriteUInt16BigEndian(response[3..], count);
                    return 5;
                }

                case FunctionCode.MaskWriteRegister:
                {
                    if (ReadOnly) return Exception(response, function, ModbusExceptionCode.IllegalFunction, session);
                    if (pdu.Length < 7) return Exception(response, function, ModbusExceptionCode.IllegalDataValue, session);
                    var address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
                    var andMask = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
                    var orMask = BinaryPrimitives.ReadUInt16BigEndian(pdu[5..]);

                    var status = _store.MaskWriteRegister(unitId, address, andMask, orMask, context);
                    if (status != 0) return Exception(response, function, status, session);

                    pdu[..7].CopyTo(response);
                    return 7;
                }

                case FunctionCode.ReadWriteMultipleRegisters:
                {
                    if (ReadOnly) return Exception(response, function, ModbusExceptionCode.IllegalFunction, session);
                    if (pdu.Length < 10) return Exception(response, function, ModbusExceptionCode.IllegalDataValue, session);
                    var readStart = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
                    var readCount = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
                    var writeStart = BinaryPrimitives.ReadUInt16BigEndian(pdu[5..]);
                    var writeCount = BinaryPrimitives.ReadUInt16BigEndian(pdu[7..]);
                    var writeBytes = pdu[9];

                    if (readCount is < 1 or > ModbusLimits.MaxReadRegisters ||
                        writeCount is < 1 or > ModbusLimits.MaxWriteRegisters ||
                        writeBytes != writeCount * 2 || pdu.Length < 10 + writeBytes)
                        return Exception(response, function, ModbusExceptionCode.IllegalDataValue, session);

                    Span<ushort> toWrite = stackalloc ushort[writeCount];
                    for (var i = 0; i < writeCount; i++)
                        toWrite[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu[(10 + i * 2)..]);

                    var writeStatus = _store.WriteRegisters(unitId, writeStart, toWrite, context);
                    if (writeStatus != 0) return Exception(response, function, writeStatus, session);

                    Span<ushort> toRead = stackalloc ushort[readCount];
                    var readStatus = _store.ReadHoldingRegisters(unitId, readStart, readCount, toRead);
                    if (readStatus != 0) return Exception(response, function, readStatus, session);

                    response[0] = function;
                    response[1] = (byte)(readCount * 2);
                    for (var i = 0; i < readCount; i++)
                        BinaryPrimitives.WriteUInt16BigEndian(response[(2 + i * 2)..], toRead[i]);
                    return 2 + readCount * 2;
                }

                case FunctionCode.ReadDeviceIdentification:
                    return DeviceIdentification(pdu, response, session);

                default:
                    return Exception(response, function, ModbusExceptionCode.IllegalFunction, session);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"server:{Name}", $"Function {function:X2} handler failed", ex);
            return Exception(response, function, ModbusExceptionCode.ServerDeviceFailure, session);
        }
    }

    /// <summary>FC43 / MEI type 14 - basic identification, so discovery tools can name us.</summary>
    private int DeviceIdentification(ReadOnlySpan<byte> pdu, Span<byte> response, ModbusClientSession session)
    {
        if (pdu.Length < 4 || pdu[1] != 0x0E)
            return Exception(response, FunctionCode.ReadDeviceIdentification, ModbusExceptionCode.IllegalFunction, session);

        var readCode = pdu[2];
        if (readCode is not (0x01 or 0x02 or 0x03 or 0x04))
            return Exception(response, FunctionCode.ReadDeviceIdentification, ModbusExceptionCode.IllegalDataValue, session);

        // Object 0/1/2 are the "basic" set and are the only ones we publish.
        var objects = new List<(byte Id, string Value)>
        {
            (0x00, Identity.VendorName),
            (0x01, Identity.ProductCode),
            (0x02, Identity.MajorMinorRevision)
        };

        response[0] = FunctionCode.ReadDeviceIdentification;
        response[1] = 0x0E;
        response[2] = 0x01;   // conformity level: basic, stream access
        response[3] = 0x00;   // more follows
        response[4] = 0x00;   // next object id
        response[5] = (byte)objects.Count;

        var offset = 6;
        foreach (var (id, value) in objects)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            if (offset + 2 + bytes.Length > ModbusLimits.MaxPduLength) break;
            response[offset++] = id;
            response[offset++] = (byte)bytes.Length;
            bytes.CopyTo(response[offset..]);
            offset += bytes.Length;
        }

        return offset;
    }

    private static int Exception(Span<byte> response, byte function, byte code, ModbusClientSession session)
    {
        session.CountError();
        response[0] = (byte)(function | FunctionCode.ExceptionFlag);
        response[1] = code;
        return 2;
    }

    private bool IsAllowed(EndPoint? endPoint)
    {
        if (AllowedClients.Count == 0) return true;
        if (endPoint is not IPEndPoint ip) return false;

        foreach (var rule in AllowedClients)
        {
            if (IpAllowList.Matches(rule, ip.Address)) return true;
        }
        return false;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
