using System.Net;
using ModbusBridge.Core.Config;
using ModbusBridge.Core.Diagnostics;
using ModbusBridge.Core.Inputs;
using ModbusBridge.Core.Outputs.Keyboard;
using ModbusBridge.Core.Modbus;
using ModbusBridge.Core.Outputs.VJoy;
using ModbusBridge.Core.Simulation;
using ModbusBridge.Core.Tags;

namespace ModbusBridge.Core.Engine;

/// <summary>A running listener plus the store behind it, so the UI can report both.</summary>
/// <summary>A derived tag with its expression already compiled and its inputs resolved.</summary>
internal sealed class DerivedRuntime
{
    public required DerivedTagConfig Config { get; init; }
    public required ModbusBridge.Core.Data.Expression Expression { get; init; }
    public required TagEntry Target { get; init; }
    public required TagEntry[] Inputs { get; init; }
    public required Func<string, double> Lookup { get; init; }
}

public sealed class ServerInstance
{
    public required ModbusServerConfig Config { get; init; }
    public required ModbusTcpServer Server { get; init; }
    public required ServerDataStore Store { get; init; }

    /// <summary>Set when the listener could not bind, e.g. port 502 already in use.</summary>
    public string? StartupError { get; set; }
}

/// <summary>
/// Owns everything that runs: the tag bus, one <see cref="DeviceRunner"/> per PLC, one listener per
/// server, and the simulator. Restarting on a config change is a full teardown and rebuild, which
/// keeps the hot path free of any "did the config change" checks.
/// </summary>
public sealed class BridgeEngine : IAsyncDisposable
{
    private readonly List<DeviceRunner> _devices = new();
    private readonly List<ServerInstance> _servers = new();
    private readonly List<VJoyFeeder> _vjoyFeeders = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private TelemetryIngestServer? _telemetry;
    private TelemetrySimulator? _simulator;
    private PcStatsCollector? _pcStats;
    private readonly List<DerivedRuntime> _derived = new();
    private KeyboardFeeder? _keyboard;
    private TagRecorder? _recorder;
    private TagReplayer? _replayer;
    private ModbusTcpServer? _virtualPlc;
    private VirtualPlcStore? _virtualPlcStore;
    private CancellationTokenSource? _housekeepingCts;
    private Task? _housekeepingTask;
    private TimerResolutionScope? _timerResolution;

    public BridgeEngine(BridgeConfig config)
    {
        Config = config;
    }

    public TagBus Tags { get; } = new();
    public BridgeConfig Config { get; private set; }
    public bool IsRunning { get; private set; }

    public IReadOnlyList<DeviceRunner> Devices => _devices;
    public IReadOnlyList<ServerInstance> Servers => _servers;
    public IReadOnlyList<VJoyFeeder> VJoyFeeders => _vjoyFeeders;
    public TelemetryIngestServer? Telemetry => _telemetry;
    public TelemetrySimulator? Simulator => _simulator;
    public PcStatsCollector? PcStats => _pcStats;
    public KeyboardFeeder? Keyboard => _keyboard;
    public TagRecorder? Recorder => _recorder;
    public TagReplayer? Replayer => _replayer;

    /// <summary>Raised after a start, stop or reload so the UI can rebind its lists.</summary>
    public event Action? TopologyChanged;

    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning) return;

            if (Config.General.HighResolutionTimer)
                _timerResolution = new TimerResolutionScope(1);

            DeclareTags();

            // Before the device runners, not after. The virtual PLC lives in here, and a
            // runner configured to poll it dials immediately on Start() - so starting the
            // simulation last meant the client raced its own server. Windows loopback was
            // forgiving enough to hide it; Linux refused the connection every time and the
            // smoke test recorded an error the Windows run never saw.
            StartSimulation();

            foreach (var clientConfig in Config.Clients)
            {
                var runner = new DeviceRunner(clientConfig, Tags);
                _devices.Add(runner);
                runner.Start();
            }

            foreach (var serverConfig in Config.Servers.Where(s => s.Enabled))
                StartServer(serverConfig);

            // Telemetry starts before the joystick so subscribed tags exist when the feeder binds.
            if (Config.Telemetry.Enabled)
            {
                _telemetry = new TelemetryIngestServer(Config.Telemetry, Tags);
                _telemetry.Start();
            }

            StartVJoy();

            _housekeepingCts = new CancellationTokenSource();
            _housekeepingTask = Task.Run(() => HousekeepingAsync(_housekeepingCts.Token));

            IsRunning = true;
            Log.Info("engine", $"Started: {_devices.Count} device(s), {_servers.Count} server(s), " +
                               $"{Tags.Count} tag(s).");
        }
        finally
        {
            _lifecycleGate.Release();
        }

        TopologyChanged?.Invoke();
    }

    private void DeclareTags()
    {
        foreach (var declaration in Config.Tags)
        {
            if (string.IsNullOrWhiteSpace(declaration.Name)) continue;
            var tag = Tags.GetOrAdd(declaration.Name);
            tag.Description ??= declaration.Description;
            tag.Units ??= declaration.Units;
            tag.DataType = declaration.DataType;
            if (declaration.InitialValue is { } initial)
                tag.Set(TagValue.Good(initial), "config");
        }
    }

    private void StartServer(ModbusServerConfig serverConfig)
    {
        var store = new ServerDataStore(serverConfig, Tags);
        var server = new ModbusTcpServer(serverConfig.Name, store)
        {
            Port = serverConfig.Port,
            MaxClients = serverConfig.MaxClients,
            ReadOnly = serverConfig.ReadOnly,
            NoDelay = serverConfig.NoDelay,
            IdleTimeoutSec = serverConfig.IdleTimeoutSec,
            AllowedClients = serverConfig.AllowedClients,
            BindAddress = IPAddress.TryParse(serverConfig.BindAddress, out var address)
                ? address
                : IPAddress.Any
        };

        var instance = new ServerInstance { Config = serverConfig, Server = server, Store = store };
        _servers.Add(instance);

        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            instance.StartupError = ex.Message;
            Log.Error("engine", $"Server '{serverConfig.Name}' could not bind {serverConfig.BindAddress}:" +
                                $"{serverConfig.Port}", ex);
        }
    }

    private void StartVJoy()
    {
        if (!Config.VJoy.Enabled) return;

        // Either backend will do. Checking only vJoy here is what made the Linux build a space
        // heater: it polled a PLC and served Modbus but could not drive a game, which is the point
        // of the rig.
        var haveBackend = VJoyInterop.IsAvailable || Outputs.Uinput.UinputDevice.IsSupported;
        if (!haveBackend)
        {
            // Not fatal: the rest of the bridge is useful without a virtual joystick, and this is
            // exactly what happens when the config is carried to a machine without vJoy installed.
            Log.Warn("vjoy", VJoyInterop.LoadError ?? "No virtual gamepad backend " +
                             "(vJoy on Windows, /dev/uinput on Linux); joystick output is off.");
            return;
        }

        foreach (var deviceConfig in Config.VJoy.Devices.Where(d => d.Enabled))
        {
            var feeder = new VJoyFeeder(deviceConfig, Tags);
            _vjoyFeeders.Add(feeder);
            feeder.Start();
        }
    }

    private void StartSimulation()
    {
        BuildDerivedTags();

        // Replay starts before recording so a capture never records its own playback by accident.
        if (Config.Replay.Enabled)
        {
            _replayer = new TagReplayer(Config.Replay, Tags);
            _replayer.Start();
        }

        if (Config.Recording.Enabled)
        {
            _recorder = new TagRecorder(Config.Recording, Tags);
            _recorder.Start();
        }

        if (Config.Keyboard.Enabled)
        {
            _keyboard = new KeyboardFeeder(Config.Keyboard, Tags);
            _keyboard.Start();
        }

        if (Config.PcStats.Enabled)
        {
            _pcStats = new PcStatsCollector(Config.PcStats, Tags);
            _pcStats.Start();
        }

        if (!Config.Simulation.Enabled) return;

        if (Config.Simulation.GenerateTelemetry)
        {
            _simulator = new TelemetrySimulator(Config.Simulation, Tags);
            _simulator.Start();
        }

        if (Config.Simulation.VirtualPlcEnabled)
        {
            _virtualPlcStore = new VirtualPlcStore();
            _virtualPlc = new ModbusTcpServer("virtual PLC", _virtualPlcStore)
            {
                Port = Config.Simulation.VirtualPlcPort,
                MaxClients = 8,
                IdleTimeoutSec = 0,
                BindAddress = IPAddress.TryParse(Config.Simulation.VirtualPlcBindAddress, out var address)
                    ? address
                    : IPAddress.Loopback
            };

            try
            {
                _virtualPlc.Start();
                Log.Info("engine", $"Virtual PLC listening on {Config.Simulation.VirtualPlcBindAddress}:" +
                                   $"{Config.Simulation.VirtualPlcPort} - point a device entry at it to test.");
            }
            catch (Exception ex)
            {
                Log.Error("engine", "Virtual PLC could not start", ex);
                _virtualPlc = null;
            }
        }
    }

    /// <summary>Background chores: staleness sweep and virtual PLC animation.</summary>
    private async Task HousekeepingAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var staleTimeout = TimeSpan.FromMilliseconds(Config.General.GlobalStaleTimeoutMs);
        var sweepCounter = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                _virtualPlcStore?.Animate();
                PublishBridgeStatus();

                // The staleness sweep is comparatively expensive; run it at 2 Hz.
                if (++sweepCounter >= 10)
                {
                    sweepCounter = 0;
                    if (staleTimeout > TimeSpan.Zero) Tags.SweepStale(staleTimeout);
                    _telemetry?.CheckTimeout();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Publishes the bridge's own state as tags, so an HMI can show protocol version, how many
    /// clients are attached and whether telemetry is fresh - things only the bridge knows.
    /// </summary>
    private void PublishBridgeStatus()
    {
        // 2.20, matching the register map the HMI was built against.
        Tags.GetOrAdd("bridge.protocolVersion").Set(TagValue.Good(2.20), "engine");

        var clients = 0;
        foreach (var server in _servers) clients += server.Server.Sessions.Count;
        Tags.GetOrAdd("bridge.connectedClients").Set(TagValue.Good(clients), "engine");

        // 65535 is the map's "never / stale for a long time" value.
        var age = 65535.0;
        if (_telemetry?.Statistics.LastFrameLocal is { } last)
            age = Math.Clamp((DateTime.Now - last).TotalMilliseconds, 0, 65535);
        Tags.GetOrAdd("bridge.telemetryAgeMs").Set(TagValue.Good(age), "engine");

        var game = _telemetry?.Statistics.GameName ?? string.Empty;
        Tags.GetOrAdd("bridge.gameCode").Set(TagValue.Good(GameCode(game)), "engine");

        PublishDerivedTags();
    }

    /// <summary>
    /// Compiles the configured expressions once. A bad expression is reported and skipped rather
    /// than failing the start: one mistyped formula should not take the whole bridge down.
    /// </summary>
    private void BuildDerivedTags()
    {
        _derived.Clear();

        foreach (var config in Config.Derived)
        {
            if (!config.Enabled) continue;
            if (string.IsNullOrWhiteSpace(config.Tag) || string.IsNullOrWhiteSpace(config.Expression))
                continue;

            if (!ModbusBridge.Core.Data.Expression.TryParse(config.Expression, out var expression, out var error))
            {
                Log.Warn("derived", $"'{config.Tag}': {error} Expression: {config.Expression}");
                continue;
            }

            var inputs = expression!.References.Select(Tags.GetOrAdd).ToArray();
            var byName = new Dictionary<string, TagEntry>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < expression.References.Count; i++) byName[expression.References[i]] = inputs[i];

            _derived.Add(new DerivedRuntime
            {
                Config = config,
                Expression = expression,
                Target = Tags.GetOrAdd(config.Tag),
                Inputs = inputs,
                // Resolved up front, so evaluation is a dictionary hit rather than a bus lookup.
                Lookup = name => byName.TryGetValue(name, out var tag) ? tag.Value.Number : 0d
            });
        }

        if (_derived.Count > 0)
            Log.Info("derived", $"{_derived.Count} derived tag(s) compiled.");
    }

    /// <summary>
    /// Evaluates the configured derived tags. Compiled once at start; evaluating here rather than
    /// on a timer of its own keeps them in step with everything else the housekeeping pass does.
    /// </summary>
    private void PublishDerivedTags()
    {
        foreach (var derived in _derived)
        {
            // A value built from a stale input is indistinguishable from a real one on a gauge,
            // so by default the whole result is withheld unless every input is trustworthy.
            if (derived.Config.RequireGoodInputs)
            {
                var usable = true;
                foreach (var input in derived.Inputs)
                {
                    if (input.Value.Quality >= ModbusBridge.Core.Data.TagQuality.Good) continue;
                    usable = false;
                    break;
                }
                if (!usable) continue;
            }

            double value;
            try { value = derived.Expression.Evaluate(derived.Lookup); }
            catch { continue; }

            if (double.IsNaN(value) || double.IsInfinity(value)) continue;
            derived.Target.Set(TagValue.Good(value), "derived");
        }
    }

    /// <summary>Game codes as the HMI's register map defines them.</summary>
    private static double GameCode(string game)
    {
        if (string.IsNullOrWhiteSpace(game)) return 0;
        var name = game.Replace(" ", string.Empty).ToLowerInvariant();
        if (name.Contains("farmingsimulator25") || name.Contains("farmingsimulator2025")) return 1;
        if (name.Contains("forzahorizon6")) return 2;
        if (name.Contains("beamng")) return 3;
        return 255;
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRunning) return;

            // Cancel, then wait for the loop to actually exit before disposing the source.
            // Disposing it while the loop still holds the token throws ObjectDisposedException on
            // a background task, which takes the process down.
            _housekeepingCts?.Cancel();
            if (_housekeepingTask is not null)
            {
                try { await _housekeepingTask.ConfigureAwait(false); } catch { }
                _housekeepingTask = null;
            }
            _housekeepingCts?.Dispose();
            _housekeepingCts = null;

            foreach (var device in _devices) await device.StopAsync().ConfigureAwait(false);
            _devices.Clear();

            foreach (var server in _servers) await server.Server.StopAsync().ConfigureAwait(false);
            _servers.Clear();

            // Release the joystick before anything else lets go, so no button is left held.
            foreach (var feeder in _vjoyFeeders) await feeder.StopAsync().ConfigureAwait(false);
            _vjoyFeeders.Clear();

            if (_telemetry is not null)
            {
                await _telemetry.StopAsync().ConfigureAwait(false);
                _telemetry = null;
            }

            if (_simulator is not null)
            {
                await _simulator.StopAsync().ConfigureAwait(false);
                _simulator = null;
            }

            if (_pcStats is not null)
            {
                await _pcStats.StopAsync().ConfigureAwait(false);
                _pcStats = null;
            }

            if (_keyboard is not null)
            {
                await _keyboard.StopAsync().ConfigureAwait(false);
                _keyboard = null;
            }

            if (_recorder is not null)
            {
                await _recorder.StopAsync().ConfigureAwait(false);
                _recorder = null;
            }

            if (_replayer is not null)
            {
                await _replayer.StopAsync().ConfigureAwait(false);
                _replayer = null;
            }

            if (_virtualPlc is not null)
            {
                await _virtualPlc.StopAsync().ConfigureAwait(false);
                _virtualPlc = null;
                _virtualPlcStore = null;
            }

            _timerResolution?.Dispose();
            _timerResolution = null;

            IsRunning = false;
            Log.Info("engine", "Stopped.");
        }
        finally
        {
            _lifecycleGate.Release();
        }

        TopologyChanged?.Invoke();
    }

    /// <summary>
    /// Applies a new configuration. Tags survive the swap, so an HMI reading a value keeps reading
    /// it across a reload as long as the tag is still mapped.
    /// </summary>
    public async Task ApplyAsync(BridgeConfig config)
    {
        var wasRunning = IsRunning;
        if (wasRunning) await StopAsync().ConfigureAwait(false);

        Config = config;
        Log.MinimumLevel = config.General.LogLevel;

        if (wasRunning) await StartAsync().ConfigureAwait(false);
        else TopologyChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycleGate.Dispose();
    }
}
