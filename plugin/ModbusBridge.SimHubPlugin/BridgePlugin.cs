using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using GameReaderCommon;
using ModbusBridge.Telemetry;
using SimHub.Plugins;

namespace ModbusBridge.SimHubPlugin
{
    /// <summary>
    /// Streams selected SimHub properties to the Modbus Telemetry Bridge, and publishes values the
    /// bridge sends back (PLC contacts, HMI buttons) as SimHub properties and events.
    ///
    /// The plugin holds no property list of its own: it advertises everything SimHub offers and the
    /// bridge replies with the subset it wants. That keeps the property browser in the bridge's UI
    /// and means changing what is streamed never requires touching SimHub.
    /// </summary>
    [PluginName("Modbus Telemetry Bridge")]
    [PluginAuthor("Modbus Telemetry Bridge")]
    [PluginDescription("Streams game telemetry to the Modbus Telemetry Bridge over UDP, and exposes "
                       + "PLC and HMI inputs from the bridge as SimHub properties and events.")]
    public class BridgePlugin : IPlugin, IDataPlugin
    {
        private const string Version = "1.0.0";

        public PluginManager PluginManager { get; set; }

        private PluginSettings _settings = new PluginSettings();
        private UdpClient _socket;
        private IPEndPoint _bridgeEndPoint;
        private Thread _receiveThread;
        private volatile bool _running;

        // Negotiated with the bridge.
        private readonly object _schemaGate = new object();
        private List<TelemetryProperty> _schema = new List<TelemetryProperty>();
        private uint _schemaId;
        private volatile bool _schemaDirty = true;

        // Feedback from the bridge, applied on SimHub's own thread inside DataUpdate.
        private readonly ConcurrentQueue<KeyValuePair<string, double>[]> _pendingFeedback =
            new ConcurrentQueue<KeyValuePair<string, double>[]>();
        private readonly Dictionary<string, double> _feedbackValues = new Dictionary<string, double>();
        private readonly HashSet<string> _registeredFeedback = new HashSet<string>();
        private readonly Dictionary<string, EventTrigger> _feedbackEvents = new Dictionary<string, EventTrigger>();

        private volatile bool _catalogRequested;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastSendMs = -1;
        private double _lastHelloMs = -1;
        private uint _sequence;
        private long _framesSent;

        // ---- Lifecycle ------------------------------------------------------------------------

        public void Init(PluginManager pluginManager)
        {
            PluginManager = pluginManager;

            string loadedFrom;
            _settings = PluginSettings.Load(out loadedFrom);
            Log($"Starting. Settings from {loadedFrom}; bridge at {_settings.BridgeHost}:{_settings.BridgePort}.");

            try
            {
                _bridgeEndPoint = new IPEndPoint(ResolveHost(_settings.BridgeHost), _settings.BridgePort);

                // Bind an ephemeral port; the bridge replies to whatever port we send from.
                _socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                IgnoreIcmpPortUnreachable(_socket);

                _running = true;
                _receiveThread = new Thread(ReceiveLoop)
                {
                    IsBackground = true,
                    Name = "ModbusBridge telemetry receive"
                };
                _receiveThread.Start();

                SendHello(null, false);
            }
            catch (Exception ex)
            {
                LogError("Could not open the telemetry socket", ex);
            }

            // Expose our own status so a dashboard can show whether the bridge is alive.
            PluginManager.AddProperty("Bridge.PluginVersion", GetType(), typeof(string), "Modbus bridge plugin version");
            PluginManager.SetPropertyValue("Bridge.PluginVersion", GetType(), Version);
            PluginManager.AddProperty("Bridge.Connected", GetType(), typeof(bool), "Bridge has subscribed to properties");
            PluginManager.SetPropertyValue("Bridge.Connected", GetType(), false);
        }

        public void End(PluginManager pluginManager)
        {
            _running = false;
            try { _socket?.Close(); } catch { }
            try { _receiveThread?.Join(500); } catch { }
            Log($"Stopped after {_framesSent} frame(s).");
        }

        /// <summary>Called by SimHub on every tick. Sends a sample and applies anything fed back.</summary>
        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            ApplyPendingFeedback();

            var nowMs = _clock.Elapsed.TotalMilliseconds;

            if (_catalogRequested)
            {
                _catalogRequested = false;
                SendCatalog();
            }

            // Keep announcing ourselves until the bridge subscribes, then use it as a keepalive.
            var helloDue = _lastHelloMs < 0 || nowMs - _lastHelloMs >= _settings.HelloIntervalMs;
            if (helloDue)
            {
                _lastHelloMs = nowMs;
                SendHello(data.GameName, data.GameRunning);
            }

            List<TelemetryProperty> schema;
            lock (_schemaGate) schema = _schema;
            if (schema.Count == 0) return;

            if (_lastSendMs >= 0 && nowMs - _lastSendMs < _settings.MinIntervalMs) return;
            _lastSendMs = nowMs;

            // The schema is resent alongside data periodically so a bridge that restarted can
            // decode us again without any handshake.
            if (_schemaDirty || _framesSent % 200 == 0)
            {
                _schemaDirty = false;
                Send(TelemetryProtocol.BuildSchema(_schemaId, schema));
            }

            SendSample(schema);
        }

        // ---- Sending --------------------------------------------------------------------------

        private void SendSample(List<TelemetryProperty> schema)
        {
            var numbers = new List<double>(schema.Count);
            var texts = new List<string>();

            foreach (var property in schema)
            {
                object value;
                try
                {
                    value = PluginManager.GetPropertyValue(property.Name);
                }
                catch (Exception)
                {
                    // A property can disappear when the game changes; send a neutral value.
                    value = null;
                }

                if (property.Type == TelemetryValueType.Text)
                    texts.Add(value?.ToString() ?? string.Empty);
                else
                    numbers.Add(ToDouble(value));
            }

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Send(TelemetryProtocol.BuildData(_schemaId, unchecked(_sequence++), timestamp, schema, numbers, texts));
            _framesSent++;
        }

        private void SendCatalog()
        {
            List<string> names;
            try
            {
                names = PluginManager.GetAllPropertiesNames() ?? new List<string>();
            }
            catch (Exception ex)
            {
                LogError("Could not enumerate SimHub properties", ex);
                return;
            }

            var properties = new List<TelemetryProperty>(names.Count);
            foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                object value = null;
                try { value = PluginManager.GetPropertyValue(name); } catch { }
                properties.Add(new TelemetryProperty(name, ClassifyValue(value)));
            }

            var chunks = TelemetryProtocol.BuildCatalog(properties);
            foreach (var chunk in chunks) Send(chunk);

            Log($"Sent a catalog of {properties.Count} propertie(s) in {chunks.Count} chunk(s).");
        }

        private void SendHello(string gameName, bool gameRunning)
        {
            Send(TelemetryProtocol.BuildHello(Version, gameName ?? string.Empty, gameRunning));
        }

        private void Send(byte[] datagram)
        {
            var socket = _socket;
            var endPoint = _bridgeEndPoint;
            if (socket == null || endPoint == null) return;

            try
            {
                socket.Send(datagram, datagram.Length, endPoint);
            }
            catch (Exception ex)
            {
                if (_settings.VerboseLogging) LogError("Send failed", ex);
            }
        }

        // ---- Receiving ------------------------------------------------------------------------

        private void ReceiveLoop()
        {
            var from = new IPEndPoint(IPAddress.Any, 0);

            while (_running)
            {
                byte[] datagram;
                try
                {
                    datagram = _socket.Receive(ref from);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException)
                {
                    if (!_running) break;
                    continue;
                }
                catch (Exception ex)
                {
                    LogError("Receive failed", ex);
                    continue;
                }

                try
                {
                    Handle(datagram);
                }
                catch (Exception ex)
                {
                    LogError("Malformed frame from the bridge", ex);
                }
            }
        }

        private void Handle(byte[] datagram)
        {
            TelemetryMessageType type;
            if (!TelemetryProtocol.TryReadHeader(datagram, datagram.Length, out type)) return;

            switch (type)
            {
                case TelemetryMessageType.Subscribe:
                    ApplySubscription(TelemetryProtocol.ReadSubscribe(datagram, datagram.Length));
                    break;

                case TelemetryMessageType.RequestCatalog:
                    // Built on SimHub's thread in DataUpdate - enumerating properties from this
                    // background thread is not worth the risk.
                    _catalogRequested = true;
                    break;

                case TelemetryMessageType.InputState:
                    var names = new List<string>();
                    var values = new List<double>();
                    TelemetryProtocol.ReadInputState(datagram, datagram.Length, names, values);

                    var pairs = new KeyValuePair<string, double>[names.Count];
                    for (int i = 0; i < names.Count && i < values.Count; i++)
                        pairs[i] = new KeyValuePair<string, double>(names[i], values[i]);
                    _pendingFeedback.Enqueue(pairs);
                    break;
            }
        }

        private void ApplySubscription(List<string> names)
        {
            var properties = new List<TelemetryProperty>(names.Count);

            foreach (var name in names)
            {
                object value = null;
                try { value = PluginManager.GetPropertyValue(name); } catch { }
                properties.Add(new TelemetryProperty(name, ClassifyValue(value)));
            }

            var id = TelemetryProtocol.ComputeSchemaId(properties);

            lock (_schemaGate)
            {
                if (_schemaId == id && _schema.Count == properties.Count) return;
                _schema = properties;
                _schemaId = id;
            }

            _schemaDirty = true;
            Log($"Bridge subscribed to {properties.Count} propertie(s); schema {id:X8}.");

            try { PluginManager.SetPropertyValue("Bridge.Connected", GetType(), properties.Count > 0); }
            catch { }
        }

        /// <summary>
        /// Publishes bridge values as SimHub properties on SimHub's own thread. New names are
        /// registered on first sight, so adding a mapping in the bridge needs no SimHub restart.
        /// </summary>
        private void ApplyPendingFeedback()
        {
            KeyValuePair<string, double>[] batch;
            var applied = false;

            while (_pendingFeedback.TryDequeue(out batch))
            {
                foreach (var pair in batch)
                {
                    if (string.IsNullOrEmpty(pair.Key)) continue;

                    var propertyName = _settings.FeedbackPrefix + "." + pair.Key;

                    if (_registeredFeedback.Add(pair.Key))
                    {
                        try
                        {
                            PluginManager.AddProperty(propertyName, GetType(), typeof(double),
                                                      "Value from the Modbus Telemetry Bridge");

                            if (_settings.RaiseEventsOnFeedback)
                            {
                                // A SimHub event can be bound to any SimHub action, which is what
                                // makes a PLC contact usable as an input inside SimHub.
                                var trigger = PluginManager.AddEvent(pair.Key, GetType());
                                if (trigger != null) _feedbackEvents[pair.Key] = trigger;
                            }
                        }
                        catch (Exception ex)
                        {
                            LogError("Could not register feedback property " + propertyName, ex);
                            continue;
                        }
                    }

                    double previous;
                    var hadPrevious = _feedbackValues.TryGetValue(pair.Key, out previous);
                    _feedbackValues[pair.Key] = pair.Value;

                    try { PluginManager.SetPropertyValue(propertyName, GetType(), pair.Value); }
                    catch { }

                    // Rising edge fires the event, matching how a physical button behaves.
                    if (_settings.RaiseEventsOnFeedback && hadPrevious &&
                        previous == 0d && pair.Value != 0d)
                    {
                        EventTrigger trigger;
                        if (_feedbackEvents.TryGetValue(pair.Key, out trigger))
                        {
                            try { trigger.Trigger(); } catch { }
                        }
                    }

                    applied = true;
                }
            }

            if (applied && _settings.VerboseLogging)
                Log($"Applied feedback for {_feedbackValues.Count} name(s).");
        }

        // ---- Helpers --------------------------------------------------------------------------

        private static IPAddress ResolveHost(string host)
        {
            IPAddress address;
            if (IPAddress.TryParse(host, out address)) return address;

            var entries = Dns.GetHostAddresses(host);
            foreach (var entry in entries)
                if (entry.AddressFamily == AddressFamily.InterNetwork) return entry;

            return IPAddress.Loopback;
        }

        private static void IgnoreIcmpPortUnreachable(UdpClient socket)
        {
            // Without this, the bridge closing makes our next Receive throw instead of returning.
            const int SIO_UDP_CONNRESET = -1744830452;
            try
            {
                socket.Client.ReceiveBufferSize = TelemetryProtocol.MaxDatagram * 8;
                socket.Client.SendBufferSize = TelemetryProtocol.MaxDatagram * 4;
            }
            catch { }

            try { socket.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); }
            catch { }
        }

        private static TelemetryValueType ClassifyValue(object value)
        {
            if (value is bool) return TelemetryValueType.Bool;
            if (value == null) return TelemetryValueType.Number;

            if (value is string) return TelemetryValueType.Text;

            if (value is double || value is float || value is decimal ||
                value is int || value is long || value is short || value is byte ||
                value is uint || value is ulong || value is ushort || value is sbyte)
                return TelemetryValueType.Number;

            // TimeSpan and DateTime are common in SimHub and are far more useful as numbers.
            if (value is TimeSpan || value is DateTime) return TelemetryValueType.Number;

            return TelemetryValueType.Text;
        }

        private static double ToDouble(object value)
        {
            if (value == null) return 0d;
            if (value is bool) return (bool)value ? 1d : 0d;
            if (value is TimeSpan) return ((TimeSpan)value).TotalSeconds;
            if (value is DateTime) return ((DateTime)value).TimeOfDay.TotalSeconds;

            try
            {
                return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                double parsed;
                var text = value.ToString();
                return double.TryParse(text, System.Globalization.NumberStyles.Any,
                                       System.Globalization.CultureInfo.InvariantCulture, out parsed)
                    ? parsed
                    : 0d;
            }
        }

        private static void Log(string message) =>
            SimHub.Logging.Current.Info("[ModbusBridge] " + message);

        private static void LogError(string message, Exception ex) =>
            SimHub.Logging.Current.Error("[ModbusBridge] " + message + ": " + ex.Message);
    }
}
