using System.Net;
using System.Net.Sockets;
using ModbusBridge.Telemetry;

namespace ModbusBridge.SmokeTest;

/// <summary>
/// Stands in for the SimHub plugin so the bridge's ingest path, the handshake and the wire format
/// can all be tested without SimHub running. It speaks the same shared protocol file the real
/// plugin does, so a format mistake fails here rather than on the rig.
/// </summary>
internal sealed class FakeSimHubPlugin : IDisposable
{
    private readonly UdpClient _socket;
    private readonly IPEndPoint _bridge;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveLoop;

    private readonly TaskCompletionSource<List<string>> _subscribed = new();
    private readonly TaskCompletionSource<bool> _catalogRequested = new();
    private readonly List<(string Name, double Value)> _feedbackReceived = new();
    private readonly object _feedbackGate = new();

    private List<TelemetryProperty> _schema = new();
    private uint _schemaId;
    private uint _sequence;

    public FakeSimHubPlugin(int bridgePort)
    {
        _bridge = new IPEndPoint(IPAddress.Loopback, bridgePort);
        _socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    /// <summary>Everything the fake claims SimHub offers.</summary>
    public List<TelemetryProperty> Catalog { get; } = new();

    public Task<List<string>> WaitForSubscription() => _subscribed.Task;
    public Task<bool> WaitForCatalogRequest() => _catalogRequested.Task;

    public IReadOnlyList<(string Name, double Value)> Feedback
    {
        get { lock (_feedbackGate) return _feedbackReceived.ToArray(); }
    }

    public void SendHello(string game = "Test Game", bool running = true) =>
        Send(TelemetryProtocol.BuildHello("test-1.0", game, running));

    public void SendCatalog()
    {
        foreach (var chunk in TelemetryProtocol.BuildCatalog(Catalog)) Send(chunk);
    }

    /// <summary>Builds a schema from the subscribed names and announces it.</summary>
    public void SendSchema(IEnumerable<string> names)
    {
        _schema = names
            .Select(n => Catalog.FirstOrDefault(p => p.Name == n) ?? new TelemetryProperty(n, TelemetryValueType.Number))
            .ToList();
        _schemaId = TelemetryProtocol.ComputeSchemaId(_schema);
        Send(TelemetryProtocol.BuildSchema(_schemaId, _schema));
    }

    /// <summary>Sends one sample. Values are supplied per property name.</summary>
    public void SendData(IReadOnlyDictionary<string, double> numbers,
                         IReadOnlyDictionary<string, string>? texts = null)
    {
        var numberValues = new List<double>();
        var textValues = new List<string>();

        foreach (var property in _schema)
        {
            if (property.Type == TelemetryValueType.Text)
                textValues.Add(texts is not null && texts.TryGetValue(property.Name, out var t) ? t : "");
            else
                numberValues.Add(numbers.TryGetValue(property.Name, out var v) ? v : 0d);
        }

        Send(TelemetryProtocol.BuildData(_schemaId, unchecked(_sequence++),
                                         DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                         _schema, numberValues, textValues));
    }

    private void Send(byte[] datagram)
    {
        try { _socket.Send(datagram, datagram.Length, _bridge); }
        catch (Exception) { /* the bridge may be stopping */ }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await _socket.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            if (!TelemetryProtocol.TryReadHeader(result.Buffer, result.Buffer.Length, out var type)) continue;

            switch (type)
            {
                case TelemetryMessageType.Subscribe:
                    _subscribed.TrySetResult(TelemetryProtocol.ReadSubscribe(result.Buffer, result.Buffer.Length));
                    break;

                case TelemetryMessageType.RequestCatalog:
                    _catalogRequested.TrySetResult(true);
                    break;

                case TelemetryMessageType.InputState:
                    var names = new List<string>();
                    var values = new List<double>();
                    TelemetryProtocol.ReadInputState(result.Buffer, result.Buffer.Length, names, values);
                    lock (_feedbackGate)
                    {
                        _feedbackReceived.Clear();
                        for (var i = 0; i < names.Count && i < values.Count; i++)
                            _feedbackReceived.Add((names[i], values[i]));
                    }
                    break;
            }
        }
    }

    private bool _disposed;

    /// <summary>
    /// Idempotent: the timeout check disposes the fake plugin deliberately part-way through, and
    /// the enclosing `using` then disposes it again.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _socket.Dispose();
        try { _receiveLoop.Wait(500); } catch { }
        _cts.Dispose();
    }
}
