using System.Net;
using System.Net.Sockets;
using ModbusBridge.Core.Config;
using ModbusBridge.Core.Data;
using ModbusBridge.Core.Diagnostics;
using ModbusBridge.Core.Tags;
using ModbusBridge.Telemetry;

namespace ModbusBridge.Core.Inputs;

/// <summary>Live counters for the telemetry link.</summary>
public sealed class TelemetryStatistics
{
    public long Frames;
    public long Dropped;
    public long CatalogChunks;
    public long FeedbackSent;

    /// <summary>Frames per second over the last second.</summary>
    public double Rate;

    /// <summary>Gap between arriving frames, in milliseconds.</summary>
    public double IntervalMs;
    public double MaxIntervalMs = double.NaN;

    /// <summary>
    /// Plugin-stamped time to bridge-received time. Both ends read the same system clock, so this
    /// is a genuine one-way figure rather than a round trip.
    /// </summary>
    public double LatencyMs;

    public DateTime? LastFrameLocal;
    public string? PluginVersion;
    public string? GameName;
    public bool GameRunning;

    internal long FramesAtLastSample;
    internal long LastSampleTicks;

    public void Reset() => MaxIntervalMs = double.NaN;
}

/// <summary>
/// Receives telemetry from the SimHub plugin over loopback UDP and publishes it to the tag bus.
/// Also serves the property browser: the plugin sends a catalog of everything SimHub offers, and
/// the bridge replies with the subset it wants streamed.
/// </summary>
public sealed class TelemetryIngestServer : IAsyncDisposable
{
    public const string WriterId = "simhub";

    private readonly TelemetryConfig _config;
    private readonly TagBus _bus;

    private readonly Dictionary<uint, List<TelemetryProperty>> _schemas = new();

    /// <summary>
    /// Keyed by SimHub property name, but holding a list: the same property can legitimately be
    /// subscribed several times to produce several tags - typically one raw and one scaled into
    /// different units. A plain dictionary here silently dropped all but the last.
    /// </summary>
    private readonly Dictionary<string, List<(TelemetrySubscription Subscription, TagEntry Tag)>> _bindingsByProperty =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(TelemetryFeedback Config, TagEntry Tag, string Name)> _feedback = new();

    private readonly object _catalogGate = new();
    private readonly Dictionary<int, List<TelemetryProperty>> _catalogChunks = new();
    private int _expectedCatalogChunks = -1;

    private UdpClient? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _feedbackLoop;
    private IPEndPoint? _pluginEndPoint;
    private long _lastFrameTicks = -1;

    private TagEntry? _connectedTag;
    private TagEntry? _gameNameTag;

    public TelemetryIngestServer(TelemetryConfig config, TagBus bus)
    {
        _config = config;
        _bus = bus;
        Bind();
    }

    public TelemetryStatistics Statistics { get; } = new();
    public bool IsRunning => _receiveLoop is not null;
    public string? Error { get; private set; }

    /// <summary>True while frames are arriving inside the configured timeout.</summary>
    public bool IsConnected =>
        _lastFrameTicks >= 0 &&
        (_config.TimeoutMs <= 0 || Clock.MsSince(_lastFrameTicks) < _config.TimeoutMs);

    /// <summary>Everything SimHub offers, as last reported. Drives the property browser.</summary>
    public IReadOnlyList<TelemetryProperty> Catalog { get; private set; } = Array.Empty<TelemetryProperty>();

    /// <summary>Raised when a complete catalog arrives, so the UI can refresh its browser.</summary>
    public event Action<IReadOnlyList<TelemetryProperty>>? CatalogReceived;

    private void Bind()
    {
        foreach (var subscription in _config.Subscriptions.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Property)))
        {
            var tag = _bus.GetOrAdd(subscription.ResolveTag(_config.TagPrefix));
            tag.Description ??= subscription.Description;
            tag.Units ??= subscription.Units;

            if (!_bindingsByProperty.TryGetValue(subscription.Property, out var bindings))
                _bindingsByProperty[subscription.Property] = bindings = new List<(TelemetrySubscription, TagEntry)>();
            bindings.Add((subscription, tag));
        }

        foreach (var feedback in _config.Feedback.Where(f => f.Enabled && !string.IsNullOrWhiteSpace(f.Tag)))
            _feedback.Add((feedback, _bus.GetOrAdd(feedback.Tag), feedback.ResolveName()));

        if (!string.IsNullOrWhiteSpace(_config.ConnectedTag))
        {
            _connectedTag = _bus.GetOrAdd(_config.ConnectedTag);
            _connectedTag.Description ??= "SimHub plugin is connected and streaming";
        }
        if (!string.IsNullOrWhiteSpace(_config.GameNameTag))
        {
            _gameNameTag = _bus.GetOrAdd(_config.GameNameTag);
            _gameNameTag.DataType = PointDataType.String;
            _gameNameTag.Description ??= "Game SimHub currently reports";
        }
    }

    public bool Start()
    {
        if (_receiveLoop is not null) return true;

        try
        {
            var address = IPAddress.TryParse(_config.BindAddress, out var parsed) ? parsed : IPAddress.Loopback;
            _socket = new UdpClient(new IPEndPoint(address, _config.ListenPort));

            // A datagram from a plugin that has since closed would otherwise kill the receive loop.
            SetIgnoreIcmpPortUnreachable(_socket);
        }
        catch (Exception ex)
        {
            Error = $"Could not listen on {_config.BindAddress}:{_config.ListenPort} - {ex.Message}";
            Log.Error(WriterId, Error);
            return false;
        }

        Error = null;
        _cts = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        if (_config.FeedbackIntervalMs > 0 && _feedback.Count > 0)
            _feedbackLoop = Task.Run(() => FeedbackLoopAsync(_cts.Token));

        Statistics.LastSampleTicks = Clock.Ticks;
        Log.Info(WriterId, $"Listening for the SimHub plugin on {_config.BindAddress}:{_config.ListenPort} " +
                           $"({_bindingsByProperty.Count} subscribed propertie(s) feeding " +
                           $"{_bindingsByProperty.Sum(b => b.Value.Count)} tag(s), " +
                           $"{_feedback.Count} fed back).");
        return true;
    }

    /// <summary>
    /// Stops Windows turning an ICMP port-unreachable into an exception on the next receive.
    /// Without this, a plugin restart takes the listener down with it.
    /// </summary>
    private static void SetIgnoreIcmpPortUnreachable(UdpClient socket)
    {
        const int SIO_UDP_CONNRESET = -1744830452;
        try
        {
            socket.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);

            // Datagrams can now be up to MaxDatagram; the default receive buffer is barely one
            // of them, so a burst (schema immediately followed by data) would drop one.
            socket.Client.ReceiveBufferSize = Math.Max(socket.Client.ReceiveBufferSize,
                                                       TelemetryProtocol.MaxDatagram * 8);
        }
        catch
        {
            // Not supported on every stack; the receive loop also catches SocketException.
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var socket = _socket!;

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                Log.Debug(WriterId, $"Receive error {ex.SocketErrorCode}; continuing.");
                continue;
            }

            try
            {
                Handle(result.Buffer, result.Buffer.Length, result.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                Statistics.Dropped++;
                Log.Warn(WriterId, $"Malformed telemetry frame from {result.RemoteEndPoint}: {ex.Message}");
            }
        }
    }

    private void Handle(byte[] buffer, int length, IPEndPoint from)
    {
        if (!TelemetryProtocol.TryReadHeader(buffer, length, out var type))
        {
            Statistics.Dropped++;
            return;
        }

        _pluginEndPoint = from;

        switch (type)
        {
            case TelemetryMessageType.Hello:
                HandleHello(buffer, length);
                break;

            case TelemetryMessageType.Catalog:
                HandleCatalog(buffer, length);
                break;

            case TelemetryMessageType.Schema:
                HandleSchema(buffer, length);
                break;

            case TelemetryMessageType.Data:
                HandleData(buffer, length);
                break;

            default:
                // Bridge-to-plugin message types are not expected inbound.
                Statistics.Dropped++;
                break;
        }
    }

    private void HandleHello(byte[] buffer, int length)
    {
        TelemetryProtocol.ReadHello(buffer, length, out var version, out var game, out var running);

        var isNew = Statistics.PluginVersion is null;
        Statistics.PluginVersion = version;
        Statistics.GameName = game;
        Statistics.GameRunning = running;

        _gameNameTag?.Set(TagValue.GoodText(game ?? ""), WriterId);

        if (isNew)
        {
            Log.Info(WriterId, $"SimHub plugin {version} connected (game: " +
                               $"{(string.IsNullOrEmpty(game) ? "none" : game)}).");
            SendSubscription();
            Send(TelemetryProtocol.BuildRequestCatalog());
        }
    }

    private void HandleCatalog(byte[] buffer, int length)
    {
        var properties = TelemetryProtocol.ReadCatalogChunk(buffer, length, out var index, out var count);
        Statistics.CatalogChunks++;

        List<TelemetryProperty>? complete = null;

        lock (_catalogGate)
        {
            if (_expectedCatalogChunks != count)
            {
                _catalogChunks.Clear();
                _expectedCatalogChunks = count;
            }

            _catalogChunks[index] = properties;

            if (_catalogChunks.Count == count)
            {
                complete = _catalogChunks.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value).ToList();
                _catalogChunks.Clear();
                _expectedCatalogChunks = -1;
            }
        }

        if (complete is null) return;

        Catalog = complete;
        Log.Info(WriterId, $"Received a catalog of {complete.Count} SimHub propertie(s).");
        try { CatalogReceived?.Invoke(complete); } catch { /* a UI handler must not break ingest */ }
    }

    private void HandleSchema(byte[] buffer, int length)
    {
        var schema = TelemetryProtocol.ReadSchema(buffer, length, out var schemaId);
        lock (_schemas) _schemas[schemaId] = schema;
        Log.Debug(WriterId, $"Schema {schemaId:X8} with {schema.Count} propertie(s).");
    }

    private void HandleData(byte[] buffer, int length)
    {
        // Peek the schema id before decoding, since decoding needs the schema.
        var peek = TelemetryProtocol.HeaderLength;
        var schemaId = TelemetryProtocol.ReadUInt32(buffer, ref peek);

        List<TelemetryProperty>? schema;
        lock (_schemas)
        {
            if (!_schemas.TryGetValue(schemaId, out schema))
            {
                // Normal for the first frames after a restart; the plugin re-sends the schema.
                Statistics.Dropped++;
                return;
            }
        }

        var sample = TelemetryProtocol.ReadData(buffer, length, schema);
        if (sample is null)
        {
            Statistics.Dropped++;
            return;
        }

        var now = Clock.Ticks;
        if (_lastFrameTicks >= 0)
        {
            Statistics.IntervalMs = Clock.ToMs(now - _lastFrameTicks);
            if (double.IsNaN(Statistics.MaxIntervalMs) || Statistics.IntervalMs > Statistics.MaxIntervalMs)
                Statistics.MaxIntervalMs = Statistics.IntervalMs;
        }
        _lastFrameTicks = now;

        Statistics.Frames++;
        Statistics.LastFrameLocal = DateTime.Now;
        Statistics.LatencyMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sample.TimestampMs;

        Publish(schema, sample);
        UpdateRate(now);

        _connectedTag?.Set(TagValue.Good(true), WriterId);
    }

    private void Publish(List<TelemetryProperty> schema, TelemetrySample sample)
    {
        int numberIndex = 0, textIndex = 0;

        foreach (var property in schema)
        {
            if (property.Type == TelemetryValueType.Text)
            {
                var text = textIndex < sample.Texts.Length ? sample.Texts[textIndex] : "";
                textIndex++;

                if (_bindingsByProperty.TryGetValue(property.Name, out var textBindings))
                    foreach (var binding in textBindings)
                        binding.Tag.Set(TagValue.GoodText(text), WriterId);
                continue;
            }

            var raw = numberIndex < sample.Numbers.Length ? sample.Numbers[numberIndex] : 0d;
            numberIndex++;

            if (!_bindingsByProperty.TryGetValue(property.Name, out var bindings)) continue;

            // Each binding gets its own scaling, so one property can feed several tags in
            // different units.
            foreach (var binding in bindings)
            {
                var value = binding.Subscription.Scale is { } scale ? scale.ToEngineering(raw) : raw;
                binding.Tag.Set(TagValue.Good(value), WriterId);
            }
        }
    }

    private void UpdateRate(long now)
    {
        var elapsedMs = Clock.ToMs(now - Statistics.LastSampleTicks);
        if (elapsedMs < 1000) return;

        Statistics.Rate = (Statistics.Frames - Statistics.FramesAtLastSample) * 1000.0 / elapsedMs;
        Statistics.FramesAtLastSample = Statistics.Frames;
        Statistics.LastSampleTicks = now;
    }

    /// <summary>Tells the plugin which properties to stream. Sent on connect and on config change.</summary>
    public void SendSubscription()
    {
        // Distinct property names: several tags may share one property.
        var names = _bindingsByProperty.Keys.ToList();
        Send(TelemetryProtocol.BuildSubscribe(names));
        Log.Info(WriterId, $"Subscribed to {names.Count} SimHub propertie(s).");
    }

    /// <summary>Asks the plugin for a fresh catalog, e.g. when the user opens the property browser.</summary>
    public bool RequestCatalog()
    {
        if (_pluginEndPoint is null) return false;
        Send(TelemetryProtocol.BuildRequestCatalog());
        return true;
    }

    private async Task FeedbackLoopAsync(CancellationToken ct)
    {
        var names = _feedback.Select(f => f.Name).ToList();
        var values = new double[_feedback.Count];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await PreciseDelay.WaitAsync(Math.Max(1, _config.FeedbackIntervalMs), ct).ConfigureAwait(false);

                if (_pluginEndPoint is null) continue;

                for (var i = 0; i < _feedback.Count; i++)
                    values[i] = _feedback[i].Tag.Value.Number;

                Send(TelemetryProtocol.BuildInputState(names, values));
                Statistics.FeedbackSent++;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(WriterId, "Feedback loop stopped", ex);
        }
    }

    private void Send(byte[] datagram)
    {
        var endPoint = _pluginEndPoint;
        var socket = _socket;
        if (endPoint is null || socket is null) return;

        try
        {
            socket.Send(datagram, datagram.Length, endPoint);
        }
        catch (Exception ex)
        {
            Log.Debug(WriterId, $"Could not send to the plugin: {ex.Message}");
        }
    }

    /// <summary>Marks telemetry tags bad once the plugin has gone quiet. Called from housekeeping.</summary>
    public void CheckTimeout()
    {
        if (_config.TimeoutMs <= 0 || _lastFrameTicks < 0) return;
        if (Clock.MsSince(_lastFrameTicks) < _config.TimeoutMs) return;

        if (_connectedTag?.Value.Bool == true)
        {
            Log.Warn(WriterId, $"No telemetry for {_config.TimeoutMs} ms; marking telemetry tags bad.");
            _connectedTag.Set(TagValue.Good(false), WriterId);
            _bus.MarkSourceBad(WriterId);
            _connectedTag.Set(TagValue.Good(false), WriterId);
        }
    }

    public async Task StopAsync()
    {
        if (_receiveLoop is null) return;

        _cts?.Cancel();
        _socket?.Dispose();

        foreach (var task in new[] { _receiveLoop, _feedbackLoop })
        {
            if (task is null) continue;
            try { await task.ConfigureAwait(false); } catch { }
        }

        _receiveLoop = null;
        _feedbackLoop = null;
        _cts?.Dispose();
        _cts = null;
        _socket = null;
        _pluginEndPoint = null;

        _connectedTag?.Set(TagValue.Good(false), WriterId);
        _bus.MarkSourceBad(WriterId);
        Log.Info(WriterId, "Telemetry ingest stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
