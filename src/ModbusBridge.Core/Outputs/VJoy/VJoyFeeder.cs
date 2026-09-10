using ModbusBridge.Core.Config;
using ModbusBridge.Core.Data;
using ModbusBridge.Core.Diagnostics;
using ModbusBridge.Core.Tags;

namespace ModbusBridge.Core.Outputs.VJoy;

/// <summary>Runtime state for one mapped button, including toggle latch and pulse timer.</summary>
internal sealed class ButtonRuntime
{
    public required VJoyButtonMapping Config { get; init; }
    public required TagEntry Tag { get; init; }

    public bool LastInput;
    public bool ToggleState;
    public long PulseUntilTicks;
    public bool HasSeenInput;
}

internal sealed class AxisRuntime
{
    public required VJoyAxisMapping Config { get; init; }
    public required TagEntry Tag { get; init; }
    public double LastNormalised = double.NaN;
}

internal sealed class PovRuntime
{
    public required VJoyPovMapping Config { get; init; }

    /// <summary>Angle tag; null when the hat is driven by contacts.</summary>
    public TagEntry? Tag { get; init; }

    /// <summary>Direction contacts, in N/E/S/W order. Any of them may be null if unmapped.</summary>
    public TagEntry?[] Contacts { get; init; } = new TagEntry?[4];

    /// <summary>Last value written, so an unchanged hat does not force a report every cycle.</summary>
    public uint LastRaw = uint.MaxValue;

    private bool Pressed(TagEntry? tag)
    {
        if (tag is null) return false;
        var on = tag.Value.Number >= Config.Threshold;
        return Config.Invert ? !on : on;
    }

    /// <summary>
    /// Resolves the four contacts to a compass position 0-7 (N, NE, E, ... NW), or null for
    /// centred. Opposing contacts cancel, which is what a real gate does when a stick is
    /// physically incapable of both.
    /// </summary>
    public int? Direction8()
    {
        var up = Pressed(Contacts[0]);
        var right = Pressed(Contacts[1]);
        var down = Pressed(Contacts[2]);
        var left = Pressed(Contacts[3]);

        if (up && down) { up = false; down = false; }
        if (left && right) { left = false; right = false; }

        if (up && right) return 1;
        if (down && right) return 3;
        if (down && left) return 5;
        if (up && left) return 7;
        if (up) return 0;
        if (right) return 2;
        if (down) return 4;
        if (left) return 6;
        return null;
    }
}

/// <summary>Live counters for the UI.</summary>
public sealed class VJoyStatistics
{
    public long Updates;
    public long Failures;
    public double LoopIntervalMs;
    public double MaxLoopIntervalMs = double.NaN;
    public int ButtonsPressed;
    public bool Released;

    public void Reset()
    {
        MaxLoopIntervalMs = double.NaN;
    }
}

/// <summary>
/// Drives one vJoy device from the tag bus. Mappings are resolved once at start; the loop then only
/// touches tag versions and pushes a report when something actually changed.
/// </summary>
public sealed class VJoyFeeder : IAsyncDisposable
{
    private readonly VJoyDeviceConfig _config;
    private readonly TagBus _bus;
    private readonly List<ButtonRuntime> _buttons = new();
    private readonly List<AxisRuntime> _axes = new();
    private readonly List<PovRuntime> _povs = new();

    private VJoyDevice? _device;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _lastLoopTicks = -1;

    public VJoyFeeder(VJoyDeviceConfig config, TagBus bus)
    {
        _config = config;
        _bus = bus;
        Bind();
    }

    public uint DeviceId => _config.DeviceId;
    public VJoyDeviceConfig Config => _config;
    public VJoyStatistics Statistics { get; } = new();
    public VJoyCapabilities? Capabilities => _device?.Capabilities;
    public bool IsRunning => _loop is not null;

    /// <summary>Why the feeder is not running, when it is not.</summary>
    public string? Error { get; private set; }

    /// <summary>Mappings that name a control the device does not have. Surfaced in the UI.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = Array.Empty<string>();

    private void Bind()
    {
        foreach (var mapping in _config.Buttons.Where(b => b.Enabled && !string.IsNullOrWhiteSpace(b.Tag)))
            _buttons.Add(new ButtonRuntime { Config = mapping, Tag = _bus.GetOrAdd(mapping.Tag) });

        foreach (var mapping in _config.Axes.Where(a => a.Enabled && !string.IsNullOrWhiteSpace(a.Tag)))
            _axes.Add(new AxisRuntime { Config = mapping, Tag = _bus.GetOrAdd(mapping.Tag) });

        foreach (var mapping in _config.Povs.Where(p => p.Enabled && p.IsUsable))
        {
            TagEntry? Resolve(string name) =>
                string.IsNullOrWhiteSpace(name) ? null : _bus.GetOrAdd(name);

            _povs.Add(new PovRuntime
            {
                Config = mapping,
                Tag = mapping.Source == VJoyPovSource.Angle ? _bus.GetOrAdd(mapping.Tag) : null,
                Contacts = mapping.Source == VJoyPovSource.Contacts
                    ? new[]
                    {
                        Resolve(mapping.UpTag), Resolve(mapping.RightTag),
                        Resolve(mapping.DownTag), Resolve(mapping.LeftTag)
                    }
                    : new TagEntry?[4]
            });
        }
    }

    public bool Start()
    {
        if (_loop is not null) return true;

        _device = VJoyDevice.Open(_config.DeviceId, out var error);
        if (_device is null)
        {
            Error = error;
            Log.Error("vjoy", $"Device {_config.DeviceId}: {error}");
            return false;
        }

        Error = null;
        Warnings = CheckMappings(_device.Capabilities);
        foreach (var warning in Warnings) Log.Warn("vjoy", warning);

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));

        Log.Info("vjoy", $"Feeding device {_config.DeviceId}: {_buttons.Count} button(s), " +
                         $"{_axes.Count} axis/axes, {_povs.Count} POV(s) every {_config.UpdateIntervalMs} ms.");
        return true;
    }

    /// <summary>Catches mappings that point at controls the device is not configured for.</summary>
    private List<string> CheckMappings(VJoyCapabilities capabilities)
    {
        var warnings = new List<string>();

        foreach (var button in _buttons)
        {
            if (button.Config.Button < 1 || button.Config.Button > capabilities.ButtonCount)
                warnings.Add($"Device {capabilities.DeviceId} has {capabilities.ButtonCount} button(s), " +
                             $"but '{button.Config.Tag}' is mapped to button {button.Config.Button}. " +
                             "Raise the button count in vJoyConf.");
        }

        foreach (var axis in _axes)
        {
            if (!capabilities.HasAxis(axis.Config.Axis))
                warnings.Add($"Device {capabilities.DeviceId} has no {axis.Config.Axis} axis, " +
                             $"but '{axis.Config.Tag}' is mapped to it. Enable the axis in vJoyConf.");
        }

        foreach (var pov in _povs)
        {
            // Continuous and discrete hats are separate pools in vJoy; a device configured for
            // one has none of the other, so the mapping's Kind has to match the hardware.
            var discrete = pov.Config.Kind == VJoyPovKind.Discrete;
            var available = discrete ? capabilities.DiscretePovCount : capabilities.ContinuousPovCount;
            var label = pov.Config.Source == VJoyPovSource.Angle
                ? $"'{pov.Config.Tag}'"
                : $"contacts [{string.Join(", ", pov.Config.ContactTags)}]";

            if (pov.Config.Pov < 1 || pov.Config.Pov > available)
            {
                var other = discrete ? capabilities.ContinuousPovCount : capabilities.DiscretePovCount;
                var hint = other > 0
                    ? $" The device does have {other} {(discrete ? "continuous" : "discrete")} POV(s) - " +
                      $"switch the mapping's Kind to match, or change the device in vJoyConf."
                    : " Enable a POV for this device in vJoyConf.";

                warnings.Add($"Device {capabilities.DeviceId} has {available} " +
                             $"{(discrete ? "discrete" : "continuous")} POV(s), but {label} is mapped " +
                             $"to POV {pov.Config.Pov}.{hint}");
            }

            if (pov.Config.Source == VJoyPovSource.Contacts && discrete
                && pov.Config.ContactTags.Count() > 2)
            {
                // Not an error - just the one surprise of discrete hats worth saying out loud.
                warnings.Add($"Device {capabilities.DeviceId} POV {pov.Config.Pov} is discrete, " +
                             $"so diagonals are rounded to the nearest of N/E/S/W.");
            }
        }

        // Two mappings fighting over one control is a config mistake worth naming.
        foreach (var clash in _buttons.GroupBy(b => b.Config.Button).Where(g => g.Count() > 1))
            warnings.Add($"Button {clash.Key} is driven by more than one tag " +
                         $"({string.Join(", ", clash.Select(b => b.Config.Tag))}); the last one wins.");

        foreach (var clash in _axes.GroupBy(a => a.Config.Axis).Where(g => g.Count() > 1))
            warnings.Add($"Axis {clash.Key} is driven by more than one tag " +
                         $"({string.Join(", ", clash.Select(a => a.Config.Tag))}); the last one wins.");

        return warnings;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var device = _device!;
        var interval = Math.Max(1, _config.UpdateIntervalMs);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = Clock.Ticks;
                if (_lastLoopTicks >= 0)
                {
                    Statistics.LoopIntervalMs = Clock.ToMs(now - _lastLoopTicks);
                    if (double.IsNaN(Statistics.MaxLoopIntervalMs) ||
                        Statistics.LoopIntervalMs > Statistics.MaxLoopIntervalMs)
                        Statistics.MaxLoopIntervalMs = Statistics.LoopIntervalMs;
                }
                _lastLoopTicks = now;

                var changed = Apply(device, now);

                if (changed || _config.AlwaysSend)
                {
                    if (device.Flush()) Statistics.Updates++;
                    else Statistics.Failures++;
                }

                await PreciseDelay.WaitAsync(interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error("vjoy", $"Feeder for device {_config.DeviceId} stopped", ex);
        }
    }

    /// <summary>Rebuilds the report from current tag values. Returns true if anything moved.</summary>
    private bool Apply(VJoyDevice device, long nowTicks)
    {
        var changed = false;
        var pressed = 0;

        // If every driving tag has gone bad, the PLC is gone - do not leave controls stuck.
        var allBad = _config.ReleaseOnBadQuality && ShouldRelease();
        if (allBad)
        {
            if (!Statistics.Released)
            {
                device.ClearAll();
                foreach (var button in _buttons) { button.ToggleState = false; button.PulseUntilTicks = 0; }
                Statistics.Released = true;
                Statistics.ButtonsPressed = 0;
                Log.Warn("vjoy", $"Device {_config.DeviceId}: driving tags went bad; released all controls.");
                return true;
            }
            return false;
        }

        if (Statistics.Released)
        {
            Statistics.Released = false;
            Log.Info("vjoy", $"Device {_config.DeviceId}: tags recovered; resuming.");
            changed = true;
        }

        foreach (var button in _buttons)
        {
            var value = button.Tag.Value;
            var raw = value.Number >= button.Config.Threshold;
            var input = raw ^ button.Config.Invert;

            bool output;
            switch (button.Config.Mode)
            {
                case VJoyButtonMode.Toggle:
                    if (input && !button.LastInput && button.HasSeenInput) button.ToggleState = !button.ToggleState;
                    output = button.ToggleState;
                    break;

                case VJoyButtonMode.Pulse:
                    if (input && !button.LastInput && button.HasSeenInput)
                        button.PulseUntilTicks = nowTicks + Clock.FromMs(Math.Max(1, button.Config.PulseMs));
                    output = nowTicks < button.PulseUntilTicks;
                    break;

                default:
                    output = input;
                    break;
            }

            button.LastInput = input;
            button.HasSeenInput = true;

            if (device.GetButton(button.Config.Button) != output)
            {
                device.SetButton(button.Config.Button, output);
                changed = true;
            }
            if (output) pressed++;
        }

        foreach (var axis in _axes)
        {
            var normalised = Normalise(axis.Config, axis.Tag.Value.Number);

            // Deadband is expressed as a fraction of full travel.
            if (!double.IsNaN(axis.LastNormalised) &&
                Math.Abs(normalised - axis.LastNormalised) < axis.Config.Deadband)
                continue;

            axis.LastNormalised = normalised;
            var raw = device.ScaleToAxis(axis.Config.Axis, normalised);
            if (device.GetAxis(axis.Config.Axis) != raw)
            {
                device.SetAxis(axis.Config.Axis, raw);
                changed = true;
            }
        }

        foreach (var pov in _povs)
        {
            // Resolve to an angle in degrees (or null for centred) regardless of source, then let
            // the hat's own kind decide how that lands in the report.
            double? degrees;
            if (pov.Config.Source == VJoyPovSource.Angle)
            {
                var value = pov.Tag!.Value.Number;
                degrees = value < 0 ? null : value;
            }
            else
            {
                var eighth = pov.Direction8();
                degrees = eighth is null ? null : eighth.Value * 45.0;
            }

            // The report carries no readback for hats, so track what was last written rather than
            // forcing an update every cycle - at a 2 ms feed that is most of the traffic.
            uint raw;
            if (pov.Config.Kind == VJoyPovKind.Discrete)
            {
                // Four positions only: round the eight-way angle to the nearest of N/E/S/W.
                int? quarter = degrees is null
                    ? null
                    : ((int)Math.Round(degrees.Value / 90.0)) & 3;
                raw = quarter is null ? 0xFFFFFFFFu : (uint)quarter.Value;
                if (raw != pov.LastRaw) { device.SetDiscretePov(pov.Config.Pov, quarter); changed = true; }
            }
            else
            {
                raw = degrees is null
                    ? 0xFFFFFFFFu
                    : (uint)(((int)Math.Round(degrees.Value * 100) % 36000 + 36000) % 36000);
                if (raw != pov.LastRaw) { device.SetContinuousPov(pov.Config.Pov, degrees); changed = true; }
            }
            pov.LastRaw = raw;
        }

        Statistics.ButtonsPressed = pressed;
        return changed;
    }

    private bool ShouldRelease()
    {
        var total = 0;
        var bad = 0;

        foreach (var button in _buttons)
        {
            total++;
            if (button.Tag.Value.Quality is TagQuality.Bad or TagQuality.Never) bad++;
        }
        foreach (var axis in _axes)
        {
            total++;
            if (axis.Tag.Value.Quality is TagQuality.Bad or TagQuality.Never) bad++;
        }

        return total > 0 && bad == total;
    }

    /// <summary>Maps a tag value onto 0..1 of axis travel, applying deadzone, curve and inversion.</summary>
    internal static double Normalise(VJoyAxisMapping config, double value)
    {
        var span = config.InputMax - config.InputMin;
        var normalised = span == 0 ? 0.5 : (value - config.InputMin) / span;
        normalised = Math.Clamp(normalised, 0.0, 1.0);

        if (config.Deadzone > 0)
        {
            // Deadzone is applied about centre, then the remaining travel is stretched back out
            // so full deflection is still reachable.
            var deadzone = Math.Clamp(config.Deadzone, 0, 0.49);
            var offset = normalised - 0.5;
            var magnitude = Math.Abs(offset) * 2;      // 0..1 from centre
            magnitude = magnitude <= deadzone ? 0 : (magnitude - deadzone) / (1 - deadzone);
            normalised = 0.5 + Math.Sign(offset) * magnitude * 0.5;
        }

        if (config.Curve is > 0 and not 1.0)
        {
            var offset = normalised - 0.5;
            var magnitude = Math.Abs(offset) * 2;
            magnitude = Math.Pow(magnitude, config.Curve);
            normalised = 0.5 + Math.Sign(offset) * magnitude * 0.5;
        }

        if (config.Invert) normalised = 1.0 - normalised;

        return Math.Clamp(normalised, 0.0, 1.0);
    }

    public async Task StopAsync()
    {
        if (_loop is null) return;

        _cts?.Cancel();
        try { await _loop.ConfigureAwait(false); } catch { }
        _loop = null;
        _cts?.Dispose();
        _cts = null;

        _device?.Dispose();
        _device = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
