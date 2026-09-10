using System.Collections.ObjectModel;
using ModbusBridge.Core.Data;

namespace ModbusBridge.Core.Config;

/// <summary>Optional up-front declaration of a tag, so it appears in the UI before any source runs.</summary>
public sealed class TagDeclaration
{
    public string Name { get; set; } = "";
    public PointDataType DataType { get; set; } = PointDataType.Float64;
    public string? Description { get; set; }
    public string? Units { get; set; }

    /// <summary>Value published at startup so consumers never see "Never".</summary>
    public double? InitialValue { get; set; }
}

public sealed class GeneralConfig
{
    public string ProfileName { get; set; } = "default";
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
    public bool StartMinimized { get; set; }
    public bool StartEnginesOnLaunch { get; set; } = true;

    /// <summary>Global sweep that demotes Good tags to Stale. 0 disables.</summary>
    public int GlobalStaleTimeoutMs { get; set; } = 5000;

    /// <summary>UI refresh rate for the live monitor.</summary>
    public int UiRefreshMs { get; set; } = 100;

    /// <summary>
    /// Raise the Windows timer resolution to 1 ms while the engine runs. Without it, every wait
    /// rounds up to ~15.6 ms, so a 10 ms poll interval actually runs at 16 ms. Costs a little
    /// extra idle power; turn it off if the bridge runs on battery.
    /// </summary>
    public bool HighResolutionTimer { get; set; } = true;

    public bool WriteLogFile { get; set; } = true;
    public int LogRetentionDays { get; set; } = 7;
}

/// <summary>Synthetic data sources for bench testing with no PLC or game attached.</summary>
public sealed class SimulationConfig
{
    public bool Enabled { get; set; }

    /// <summary>Spin up a fake Modbus TCP server so a client device entry has something to talk to.</summary>
    public bool VirtualPlcEnabled { get; set; }
    public string VirtualPlcBindAddress { get; set; } = "127.0.0.1";
    public int VirtualPlcPort { get; set; } = 15020;

    /// <summary>Drive declared tags with test waveforms when no real source owns them.</summary>
    public bool GenerateTelemetry { get; set; }
    public ObservableCollection<SimulatedTagConfig> Tags { get; set; } = new();
}

public sealed class SimulatedTagConfig
{
    public string Tag { get; set; } = "";

    /// <summary>sine | triangle | sawtooth | square | ramp | random | constant</summary>
    public string Waveform { get; set; } = "sine";
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    public double PeriodSec { get; set; } = 10;
}

/// <summary>Root of the on-disk configuration.</summary>
public sealed class BridgeConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public GeneralConfig General { get; set; } = new();
    public ObservableCollection<TagDeclaration> Tags { get; set; } = new();
    public ObservableCollection<ModbusClientConfig> Clients { get; set; } = new();
    public ObservableCollection<ModbusServerConfig> Servers { get; set; } = new();

    /// <summary>Virtual joystick output - tags to buttons, axes and hats.</summary>
    public VJoyConfig VJoy { get; set; } = new();

    /// <summary>Game telemetry ingest from the SimHub plugin.</summary>
    public TelemetryConfig Telemetry { get; set; } = new();

    public SimulationConfig Simulation { get; set; } = new();

    public PcStatsConfig PcStats { get; set; } = new();

    public KeyboardConfig Keyboard { get; set; } = new();

    public RecordingConfig Recording { get; set; } = new();

    public ReplayConfig Replay { get; set; } = new();

    /// <summary>Tags computed from other tags. Evaluated in order, so one may build on another.</summary>
    public ObservableCollection<DerivedTagConfig> Derived { get; set; } = new();

    /// <summary>Returns human-readable problems; an empty list means the config is usable.</summary>
    public List<string> Validate()
    {
        var problems = new List<string>();

        foreach (var duplicate in Clients.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                                         .Where(g => g.Count() > 1))
            problems.Add($"Duplicate client id '{duplicate.Key}'.");

        foreach (var duplicate in Servers.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
                                         .Where(g => g.Count() > 1))
            problems.Add($"Duplicate server id '{duplicate.Key}'.");

        foreach (var client in Clients)
        {
            if (string.IsNullOrWhiteSpace(client.Host))
                problems.Add($"Client '{client.Name}': host is empty.");
            if (client.Port is < 1 or > 65535)
                problems.Add($"Client '{client.Name}': port {client.Port} is out of range.");

            foreach (var group in client.ReadGroups)
            {
                if (group.Count < 1)
                    problems.Add($"Client '{client.Name}' / read group '{group.Name}': count must be at least 1.");
                if (group.PollIntervalMs < 1)
                    problems.Add($"Client '{client.Name}' / read group '{group.Name}': poll interval must be at least 1 ms.");
                foreach (var point in group.Points)
                    ValidatePoint(problems, $"Client '{client.Name}' / read group '{group.Name}'", point, group.Count, group.Area);
            }

            foreach (var group in client.WriteGroups)
            {
                var span = group.Count > 0 ? group.Count : SpanOf(group.Points);
                foreach (var point in group.Points)
                    ValidatePoint(problems, $"Client '{client.Name}' / write group '{group.Name}'", point, span, group.Area);
            }
        }

        foreach (var server in Servers)
        {
            if (server.Port is < 1 or > 65535)
                problems.Add($"Server '{server.Name}': port {server.Port} is out of range.");

            foreach (var unitGroup in server.Maps.Where(m => m.Enabled)
                                                 .GroupBy(m => m.UnitId)
                                                 .Where(g => g.Count() > 1))
                problems.Add($"Server '{server.Name}': unit id {unitGroup.Key} is mapped more than once.");

            foreach (var map in server.Maps)
            {
                foreach (var areaGroup in map.Blocks.Where(b => b.Enabled).GroupBy(b => b.Area))
                {
                    var ordered = areaGroup.OrderBy(b => b.StartAddress).ToList();
                    for (var i = 1; i < ordered.Count; i++)
                    {
                        var previous = ordered[i - 1];
                        if (previous.StartAddress + previous.Size > ordered[i].StartAddress)
                            problems.Add($"Server '{server.Name}' / map '{map.Name}': blocks " +
                                         $"'{previous.Name}' and '{ordered[i].Name}' overlap in {areaGroup.Key}.");
                    }
                }

                foreach (var block in map.Blocks)
                    foreach (var point in block.Points)
                        ValidatePoint(problems, $"Server '{server.Name}' / map '{map.Name}' / block '{block.Name}'",
                                      point, block.Size, block.Area);
            }
        }

        if (Telemetry.Enabled)
        {
            if (Telemetry.ListenPort is < 1 or > 65535)
                problems.Add($"Telemetry listen port {Telemetry.ListenPort} is out of range.");

            foreach (var duplicate in Telemetry.Subscriptions
                         .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Property))
                         .GroupBy(s => s.Property, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
                problems.Add($"SimHub property '{duplicate.Key}' is subscribed more than once.");

            foreach (var duplicate in Telemetry.Subscriptions
                         .Where(s => s.Enabled)
                         .GroupBy(s => s.ResolveTag(Telemetry.TagPrefix), StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
                problems.Add($"Telemetry tag '{duplicate.Key}' is written by more than one subscription.");

            foreach (var feedback in Telemetry.Feedback.Where(f => f.Enabled))
            {
                if (string.IsNullOrWhiteSpace(feedback.Tag))
                    problems.Add("A SimHub feedback entry has no tag.");
            }
        }

        foreach (var device in VJoy.Devices)
        {
            if (device.DeviceId is < 1 or > 16)
                problems.Add($"vJoy device id {device.DeviceId} is out of range; valid ids are 1-16.");
            if (device.UpdateIntervalMs < 1)
                problems.Add($"vJoy device {device.DeviceId}: update interval must be at least 1 ms.");

            foreach (var button in device.Buttons.Where(b => b.Enabled))
            {
                if (string.IsNullOrWhiteSpace(button.Tag))
                    problems.Add($"vJoy device {device.DeviceId}: button {button.Button} has no tag.");
                if (button.Button is < 1 or > 128)
                    problems.Add($"vJoy device {device.DeviceId}: button {button.Button} is out of range (1-128).");
                if (button.Mode == VJoyButtonMode.Pulse && button.PulseMs < 1)
                    problems.Add($"vJoy device {device.DeviceId}: button {button.Button} is a pulse " +
                                 "but its pulse length is not at least 1 ms.");
            }

            foreach (var axis in device.Axes.Where(a => a.Enabled))
            {
                if (string.IsNullOrWhiteSpace(axis.Tag))
                    problems.Add($"vJoy device {device.DeviceId}: axis {axis.Axis} has no tag.");
                if (axis.InputMin == axis.InputMax)
                    problems.Add($"vJoy device {device.DeviceId}: axis {axis.Axis} has an empty input range " +
                                 $"({axis.InputMin}..{axis.InputMax}).");
                if (axis.Deadzone is < 0 or > 0.49)
                    problems.Add($"vJoy device {device.DeviceId}: axis {axis.Axis} deadzone must be 0..0.49.");
                if (axis.Curve <= 0)
                    problems.Add($"vJoy device {device.DeviceId}: axis {axis.Axis} curve must be greater than 0.");
            }
        }

        return problems;
    }

    private static int SpanOf(IReadOnlyCollection<PointConfig> points) =>
        points.Count == 0 ? 0 : points.Max(p => p.Offset + p.Size);

    private static void ValidatePoint(List<string> problems, string where, PointConfig point, int span, ModbusArea area)
    {
        if (string.IsNullOrWhiteSpace(point.Tag))
        {
            problems.Add($"{where}: a point at offset {point.Offset} has no tag name.");
            return;
        }

        if (point.Offset < 0)
            problems.Add($"{where} / '{point.Tag}': offset must not be negative.");

        if (span > 0 && point.Offset + point.Size > span)
            problems.Add($"{where} / '{point.Tag}': offset {point.Offset} + size {point.Size} exceeds the block span of {span}.");

        var isBitArea = area is ModbusArea.Coil or ModbusArea.DiscreteInput;
        if (isBitArea && point.DataType != PointDataType.Bool)
            problems.Add($"{where} / '{point.Tag}': {area} points must be Bool.");

        if (!isBitArea && point.DataType == PointDataType.Bool && point.BitIndex is < 0 or > 15)
            problems.Add($"{where} / '{point.Tag}': a Bool in a register area needs a bitIndex of 0-15.");

        if (point.DataType == PointDataType.String && point.Length < 1)
            problems.Add($"{where} / '{point.Tag}': String points need a length in registers.");
    }
}

