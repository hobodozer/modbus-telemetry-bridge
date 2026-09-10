using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ModbusBridge.Core.Config;
using ModbusBridge.Core.Data;
using ModbusBridge.Core.Diagnostics;
using ModbusBridge.Core.Engine;
using ModbusBridge.Core.Outputs.VJoy;
using ModbusBridge.Core.Tags;

namespace ModbusBridge.App.ViewModels;

/// <summary>
/// Application state: owns the engine and the config, drives the UI refresh timer, and exposes the
/// collections the tabs bind to.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly DispatcherTimer _uiTimer = new();
    private readonly Dictionary<string, TagRowViewModel> _tagRows = new(StringComparer.OrdinalIgnoreCase);

    private BridgeEngine _engine;
    private long _lastTagStructureVersion = -1;
    private string _tagFilter = "";
    private LogLevel _logFilter = LogLevel.Info;
    private string _status = "Ready.";
    private bool _configDirty;

    public MainViewModel(ConfigService configService, BridgeConfig config)
    {
        _configService = configService;
        Config = config;
        _engine = new BridgeEngine(config);

        Clients = new ObservableCollection<ModbusClientConfig>(config.Clients);
        Servers = new ObservableCollection<ModbusServerConfig>(config.Servers);
        VJoyDevices = new ObservableCollection<VJoyDeviceConfig>(config.VJoy.Devices);
        Clients.CollectionChanged += (_, _) => MarkDirty();
        Servers.CollectionChanged += (_, _) => MarkDirty();
        VJoyDevices.CollectionChanged += (_, _) => MarkDirty();

        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning);
        RestartCommand = new AsyncRelayCommand(RestartAsync);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync);
        SaveCommand = new RelayCommand(Save);
        ReloadCommand = new AsyncRelayCommand(ReloadFromDiskAsync);
        OpenConfigFolderCommand = new RelayCommand(OpenConfigFolder);
        ClearLogCommand = new RelayCommand(() => { Log.Clear(); LogRows.Clear(); });
        ResetAllLatencyCommand = new RelayCommand(() =>
        {
            foreach (var device in DeviceStatuses) device.Runner.Statistics.ResetLatency();
        });

        Log.Entry += OnLogEntry;
        _engine.TopologyChanged += OnTopologyChanged;

        _uiTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, config.General.UiRefreshMs));
        _uiTimer.Tick += (_, _) => RefreshLiveData();
        _uiTimer.Start();

        foreach (var entry in Log.Recent(300)) LogRows.Add(new LogRowViewModel(entry));
    }

    public BridgeConfig Config { get; private set; }
    public BridgeEngine Engine => _engine;
    public string ConfigPath => _configService.ConfigPath;

    // ---- Collections the tabs bind to ----
    public ObservableCollection<ModbusClientConfig> Clients { get; }
    public ObservableCollection<ModbusServerConfig> Servers { get; }
    public ObservableCollection<VJoyDeviceConfig> VJoyDevices { get; }
    public ObservableCollection<DeviceStatusViewModel> DeviceStatuses { get; } = new();
    public ObservableCollection<ServerStatusViewModel> ServerStatuses { get; } = new();
    public ObservableCollection<VJoyStatusViewModel> VJoyStatuses { get; } = new();
    public ObservableCollection<TelemetryStatusViewModel> TelemetryStatuses { get; } = new();
    public ObservableCollection<TagRowViewModel> VisibleTags { get; } = new();
    public ObservableCollection<LogRowViewModel> LogRows { get; } = new();

    /// <summary>Operator console: one lamp per mapped vJoy button, one bar per axis.</summary>
    public ObservableCollection<PanelLampViewModel> PanelLamps { get; } = new();
    public ObservableCollection<PanelAxisViewModel> PanelAxes { get; } = new();

    private string _panelState = "no vJoy device";
    public string PanelState
    {
        get => _panelState;
        private set => Set(ref _panelState, value);
    }

    // ---- Commands ----
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand RestartCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public RelayCommand SaveCommand { get; }
    public AsyncRelayCommand ReloadCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand ResetAllLatencyCommand { get; }

    public bool IsRunning => _engine.IsRunning;

    public string EngineStateText => _engine.IsRunning ? "Running" : "Stopped";

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public bool ConfigDirty
    {
        get => _configDirty;
        private set { if (Set(ref _configDirty, value)) Raise(nameof(WindowTitle)); }
    }

    public string WindowTitle =>
        $"Modbus Telemetry Bridge - {Config.General.ProfileName}{(ConfigDirty ? " *" : "")}";

    public string TagFilter
    {
        get => _tagFilter;
        set { if (Set(ref _tagFilter, value)) RebuildTagList(); }
    }

    public LogLevel LogFilter
    {
        get => _logFilter;
        set => Set(ref _logFilter, value);
    }

    public IReadOnlyList<LogLevel> LogLevels { get; } =
        Enum.GetValues<LogLevel>().ToArray();

    public IReadOnlyList<PointDataType> DataTypes { get; } =
        Enum.GetValues<PointDataType>().ToArray();

    public IReadOnlyList<ModbusArea> Areas { get; } =
        Enum.GetValues<ModbusArea>().ToArray();

    public IReadOnlyList<AccessMode> AccessModes { get; } =
        Enum.GetValues<AccessMode>().ToArray();

    public IReadOnlyList<WriteMode> WriteModes { get; } =
        Enum.GetValues<WriteMode>().ToArray();

    public IReadOnlyList<StaleBehavior> StaleBehaviors { get; } =
        Enum.GetValues<StaleBehavior>().ToArray();

    public IReadOnlyList<WordOrder> WordOrders { get; } =
        Enum.GetValues<WordOrder>().ToArray();

    public IReadOnlyList<ByteOrder> ByteOrders { get; } =
        Enum.GetValues<ByteOrder>().ToArray();

    public void MarkDirty() => ConfigDirty = true;

    // ---- Engine lifecycle ----

    public async Task StartAsync()
    {
        Status = "Starting...";
        await _engine.StartAsync();
        Raise(nameof(IsRunning));
        Raise(nameof(EngineStateText));
        Status = "Running.";
    }

    public async Task StopAsync()
    {
        Status = "Stopping...";
        await _engine.StopAsync();
        Raise(nameof(IsRunning));
        Raise(nameof(EngineStateText));
        Status = "Stopped.";
    }

    private async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    /// <summary>Validates the edited config, saves it and restarts the engine on it.</summary>
    private async Task ApplyAsync()
    {
        CommitEdits();

        var problems = Config.Validate();
        if (problems.Count > 0)
        {
            var detail = string.Join(Environment.NewLine, problems.Take(15));
            if (problems.Count > 15) detail += $"{Environment.NewLine}... and {problems.Count - 15} more.";
            MessageBox.Show(detail, $"{problems.Count} configuration problem(s)",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            Status = $"Not applied - {problems.Count} problem(s).";
            return;
        }

        try
        {
            _configService.Save(Config);
            ConfigDirty = false;
        }
        catch (Exception ex)
        {
            Log.Error("ui", "Could not save the configuration", ex);
            Status = "Save failed - see the log.";
            return;
        }

        Status = "Applying...";
        await _engine.ApplyAsync(Config);
        Raise(nameof(IsRunning));
        Raise(nameof(EngineStateText));
        Raise(nameof(WindowTitle));
        Status = "Configuration applied.";
    }

    private void Save()
    {
        CommitEdits();
        var problems = Config.Validate();
        if (problems.Count > 0)
            Log.Warn("ui", $"Saving a configuration with {problems.Count} problem(s); " +
                           $"first: {problems[0]}");
        try
        {
            _configService.Save(Config);
            ConfigDirty = false;
            Status = $"Saved {Path.GetFileName(ConfigPath)}.";
        }
        catch (Exception ex)
        {
            Log.Error("ui", "Could not save the configuration", ex);
            Status = "Save failed - see the log.";
        }
    }

    /// <summary>Copies the edited collections back into the config object.</summary>
    private void CommitEdits()
    {
        Config.Clients = new ObservableCollection<ModbusClientConfig>(Clients);
        Config.Servers = new ObservableCollection<ModbusServerConfig>(Servers);
        Config.VJoy.Devices = new ObservableCollection<VJoyDeviceConfig>(VJoyDevices);
    }

    private async Task ReloadFromDiskAsync()
    {
        if (ConfigDirty &&
            MessageBox.Show("Discard your unsaved changes and reload from disk?", "Reload configuration",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var config = _configService.Load();
            await AdoptAsync(config);
            Status = "Reloaded from disk.";
        }
        catch (Exception ex)
        {
            Log.Error("ui", "Could not reload the configuration", ex);
            Status = "Reload failed - see the log.";
        }
    }

    /// <summary>Takes on a config loaded elsewhere (disk watcher or reload) and restarts on it.</summary>
    public async Task AdoptAsync(BridgeConfig config)
    {
        Config = config;

        Clients.Clear();
        foreach (var client in config.Clients) Clients.Add(client);
        Servers.Clear();
        foreach (var server in config.Servers) Servers.Add(server);
        VJoyDevices.Clear();
        foreach (var device in config.VJoy.Devices) VJoyDevices.Add(device);

        await _engine.ApplyAsync(config);

        ConfigDirty = false;
        Raise(nameof(Config));
        Raise(nameof(IsRunning));
        Raise(nameof(EngineStateText));
        Raise(nameof(WindowTitle));
    }

    private void OpenConfigFolder()
    {
        var folder = Path.GetDirectoryName(ConfigPath);
        if (string.IsNullOrEmpty(folder)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("ui", $"Could not open the config folder: {ex.Message}");
        }
    }

    // ---- Live refresh ----

    private void OnTopologyChanged() =>
        Application.Current?.Dispatcher.BeginInvoke(RebuildStatusLists);

    private void RebuildStatusLists()
    {
        DeviceStatuses.Clear();
        foreach (var device in _engine.Devices) DeviceStatuses.Add(new DeviceStatusViewModel(device));

        ServerStatuses.Clear();
        foreach (var server in _engine.Servers) ServerStatuses.Add(new ServerStatusViewModel(server));

        VJoyStatuses.Clear();
        foreach (var feeder in _engine.VJoyFeeders) VJoyStatuses.Add(new VJoyStatusViewModel(feeder));

        RebuildPanel();
        Raise(nameof(VJoyDriverStatus));

        TelemetryStatuses.Clear();
        if (_engine.Telemetry is { } telemetry)
            TelemetryStatuses.Add(new TelemetryStatusViewModel(telemetry));

        _lastTagStructureVersion = -1;   // force the tag list to rebuild
    }

    /// <summary>Driver-level vJoy state, shown whether or not any feeder is configured.</summary>
    public string VJoyDriverStatus => VJoyInterop.IsAvailable
        ? $"vJoy {VJoyInterop.ProductString()} available (API 0x{VJoyInterop.GetvJoyVersion():X})"
        : VJoyInterop.LoadError ?? "vJoy unavailable";

    public Brush VJoyDriverBrush => VJoyInterop.IsAvailable ? Brushes.MediumSeaGreen : Brushes.Goldenrod;

    /// <summary>
    /// Builds the operator console from the vJoy mappings, so the lamps show what the game sees
    /// rather than what the PLC sent - a mapping mistake then reads as a lamp that never lights.
    /// </summary>
    private void RebuildPanel()
    {
        PanelLamps.Clear();
        PanelAxes.Clear();

        var device = Config.VJoy.Devices.FirstOrDefault();
        if (device is null) { PanelState = "no vJoy device configured"; return; }

        foreach (var button in device.Buttons.Where(b => b.Enabled && !string.IsNullOrWhiteSpace(b.Tag))
                                             .OrderBy(b => b.Button))
            PanelLamps.Add(new PanelLampViewModel(button.Button, button.Tag, _engine.Tags.Find(button.Tag)));

        foreach (var axis in device.Axes.Where(a => a.Enabled && !string.IsNullOrWhiteSpace(a.Tag)))
            PanelAxes.Add(new PanelAxisViewModel(axis, _engine.Tags.Find(axis.Tag)));

        RefreshPanelState();
    }

    private void RefreshPanelState()
    {
        var feeder = _engine.VJoyFeeders.FirstOrDefault();
        if (feeder is null) { PanelState = "vJoy not running"; return; }

        var profile = string.IsNullOrEmpty(feeder.Statistics.ActiveProfile)
            ? "none" : feeder.Statistics.ActiveProfile;
        var layer = string.IsNullOrEmpty(feeder.Statistics.ActiveLayer)
            ? "base" : feeder.Statistics.ActiveLayer;

        PanelState = $"profile: {profile}    layer: {layer}    {PanelLamps.Count(l => l.IsOn)} pressed";
    }

    private void RefreshLiveData()
    {
        foreach (var device in DeviceStatuses) device.Refresh();
        foreach (var server in ServerStatuses) server.Refresh();
        foreach (var vjoy in VJoyStatuses) vjoy.Refresh();
        foreach (var telemetry in TelemetryStatuses) telemetry.Refresh();

        if (_engine.Tags.StructureVersion != _lastTagStructureVersion)
        {
            _lastTagStructureVersion = _engine.Tags.StructureVersion;
            RebuildTagList();
        }

        foreach (var row in VisibleTags) row.Refresh();

        foreach (var lamp in PanelLamps) lamp.Refresh();
        foreach (var axis in PanelAxes) axis.Refresh();
        RefreshPanelState();
    }

    private void RebuildTagList()
    {
        var filter = _tagFilter.Trim();

        foreach (var entry in _engine.Tags.Snapshot())
        {
            if (!_tagRows.ContainsKey(entry.Name))
                _tagRows[entry.Name] = new TagRowViewModel(entry);
        }

        var matches = _engine.Tags.Snapshot()
            .Where(t => filter.Length == 0 ||
                        t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        (t.Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(t => _tagRows[t.Name])
            .ToList();

        VisibleTags.Clear();
        foreach (var row in matches) VisibleTags.Add(row);
        Raise(nameof(TagCountText));
    }

    public string TagCountText => $"{VisibleTags.Count} of {_engine.Tags.Count} tag(s)";

    // ---- Tag actions ----

    public void ForceTag(TagRowViewModel row, double value)
    {
        row.Entry.Force(TagValue.Good(value));
        Log.Info("ui", $"Forced '{row.Name}' to {value}.");
        row.Refresh();
    }

    public void UnforceTag(TagRowViewModel row)
    {
        row.Entry.Unforce();
        Log.Info("ui", $"Released the force on '{row.Name}'.");
        row.Refresh();
    }

    public void WriteTag(TagRowViewModel row, double value)
    {
        row.Entry.Set(TagValue.Good(value), "ui");
        row.Refresh();
    }

    private void OnLogEntry(LogEntry entry)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.BeginInvoke(() =>
        {
            if (entry.Level < LogFilter) return;
            LogRows.Add(new LogRowViewModel(entry));
            while (LogRows.Count > 2000) LogRows.RemoveAt(0);
        });
    }

    public async Task ShutdownAsync()
    {
        _uiTimer.Stop();
        Log.Entry -= OnLogEntry;
        await _engine.DisposeAsync();
    }
}
