using ModbusBridge.Core.Diagnostics;
using ModbusBridge.Core.Outputs;

namespace ModbusBridge.Core.Outputs.VJoy;

/// <summary>
/// One acquired vJoy device. Reports are built in a <see cref="JoystickPositionV2"/> and pushed with
/// a single <c>UpdateVJD</c> call, which costs one driver transition regardless of how many buttons
/// changed.
/// </summary>
public sealed class VJoyDevice : IGamepadDevice
{
    private JoystickPositionV2 _report;
    private bool _acquired;

    private VJoyDevice(uint deviceId, GamepadCapabilities capabilities)
    {
        DeviceId = deviceId;
        Capabilities = capabilities;
        _report = new JoystickPositionV2 { bDevice = (byte)deviceId };
        CentreAllPovs();
    }

    public uint DeviceId { get; }
    public GamepadCapabilities Capabilities { get; }

    /// <summary>Number of UpdateVJD calls that failed since open — a non-zero value means trouble.</summary>
    public long UpdateFailures { get; private set; }

    /// <summary>
    /// Acquires a device. Returns null with a reason in <paramref name="error"/> rather than
    /// throwing, because "someone else owns device 1" is an ordinary, recoverable situation.
    /// </summary>
    public static VJoyDevice? Open(uint deviceId, out string? error)
    {
        error = null;

        if (!VJoyInterop.IsAvailable)
        {
            error = VJoyInterop.LoadError ?? "vJoy is not available.";
            return null;
        }

        if (deviceId is < 1 or > 16)
        {
            error = $"vJoy device id {deviceId} is out of range; valid ids are 1-16.";
            return null;
        }

        var status = VJoyInterop.GetVJDStatus(deviceId);
        switch (status)
        {
            case VjdStatus.Missing:
                error = $"vJoy device {deviceId} does not exist. Add it in vJoyConf and reboot if prompted.";
                return null;
            case VjdStatus.Busy:
                error = $"vJoy device {deviceId} is already owned by another application.";
                return null;
            case VjdStatus.Unknown:
                error = $"vJoy device {deviceId} is in an unknown state.";
                return null;
        }

        if (status != VjdStatus.Own && !VJoyInterop.AcquireVJD(deviceId))
        {
            error = $"Could not acquire vJoy device {deviceId}.";
            return null;
        }

        var capabilities = ReadCapabilities(deviceId);
        var device = new VJoyDevice(deviceId, capabilities) { _acquired = true };

        VJoyInterop.ResetVJD(deviceId);
        device.CentreAllAxes();
        device.Flush();

        Log.Info("vjoy", $"Acquired {capabilities}");
        return device;
    }

    /// <summary>
    /// Reads what a device offers without acquiring it, so the UI can ask "does this device have a
    /// continuous hat?" while the feeder owns it. Null when vJoy is not installed.
    /// </summary>
    public static GamepadCapabilities? Probe(uint deviceId) =>
        VJoyInterop.IsAvailable ? ReadCapabilities(deviceId) : null;

    private static GamepadCapabilities ReadCapabilities(uint deviceId)
    {
        var axes = new Dictionary<VJoyAxis, (long Min, long Max)>();
        foreach (var axis in Enum.GetValues<VJoyAxis>())
        {
            if (!VJoyInterop.GetVJDAxisExist(deviceId, (uint)axis)) continue;

            long min = 0, max = 32767;
            VJoyInterop.GetVJDAxisMin(deviceId, (uint)axis, ref min);
            VJoyInterop.GetVJDAxisMax(deviceId, (uint)axis, ref max);
            axes[axis] = (min, max);
        }

        return new GamepadCapabilities
        {
            DeviceId = deviceId,
            ButtonCount = VJoyInterop.GetVJDButtonNumber(deviceId),
            ContinuousPovCount = VJoyInterop.GetVJDContPovNumber(deviceId),
            DiscretePovCount = VJoyInterop.GetVJDDiscPovNumber(deviceId),
            Axes = axes,
            Driver = "vJoy"
        };
    }

    // ---- Building a report ------------------------------------------------------------------

    /// <summary>Sets button <paramref name="button"/> (1-based, as vJoy numbers them).</summary>
    public void SetButton(int button, bool pressed)
    {
        if (button < 1 || button > 128) return;

        var index = button - 1;
        var mask = 1u << (index % 32);

        switch (index / 32)
        {
            case 0: _report.lButtons = pressed ? _report.lButtons | mask : _report.lButtons & ~mask; break;
            case 1: _report.lButtonsEx1 = pressed ? _report.lButtonsEx1 | mask : _report.lButtonsEx1 & ~mask; break;
            case 2: _report.lButtonsEx2 = pressed ? _report.lButtonsEx2 | mask : _report.lButtonsEx2 & ~mask; break;
            case 3: _report.lButtonsEx3 = pressed ? _report.lButtonsEx3 | mask : _report.lButtonsEx3 & ~mask; break;
        }
    }

    public bool GetButton(int button)
    {
        if (button < 1 || button > 128) return false;
        var index = button - 1;
        var mask = 1u << (index % 32);
        return (index / 32) switch
        {
            0 => (_report.lButtons & mask) != 0,
            1 => (_report.lButtonsEx1 & mask) != 0,
            2 => (_report.lButtonsEx2 & mask) != 0,
            3 => (_report.lButtonsEx3 & mask) != 0,
            _ => false
        };
    }

    /// <summary>Sets an axis to a raw driver value. Use <see cref="ScaleToAxis"/> to get one.</summary>
    public void SetAxis(VJoyAxis axis, int value)
    {
        switch (axis)
        {
            case VJoyAxis.X: _report.wAxisX = value; break;
            case VJoyAxis.Y: _report.wAxisY = value; break;
            case VJoyAxis.Z: _report.wAxisZ = value; break;
            case VJoyAxis.RX: _report.wAxisXRot = value; break;
            case VJoyAxis.RY: _report.wAxisYRot = value; break;
            case VJoyAxis.RZ: _report.wAxisZRot = value; break;
            case VJoyAxis.Slider0: _report.wSlider = value; break;
            case VJoyAxis.Slider1: _report.wDial = value; break;
        }
    }

    public int GetAxis(VJoyAxis axis) => axis switch
    {
        VJoyAxis.X => _report.wAxisX,
        VJoyAxis.Y => _report.wAxisY,
        VJoyAxis.Z => _report.wAxisZ,
        VJoyAxis.RX => _report.wAxisXRot,
        VJoyAxis.RY => _report.wAxisYRot,
        VJoyAxis.RZ => _report.wAxisZRot,
        VJoyAxis.Slider0 => _report.wSlider,
        VJoyAxis.Slider1 => _report.wDial,
        _ => 0
    };

    /// <summary>Maps a 0..1 normalised value onto the raw range the driver reported for that axis.</summary>
    public int ScaleToAxis(VJoyAxis axis, double normalised)
    {
        if (!Capabilities.Axes.TryGetValue(axis, out var range)) return 0;
        normalised = Math.Clamp(normalised, 0.0, 1.0);
        return (int)Math.Round(range.Min + normalised * (range.Max - range.Min));
    }

    /// <summary>Continuous POV in degrees, or null for centred.</summary>
    public void SetContinuousPov(int pov, double? degrees)
    {
        // The driver takes hundredths of a degree; 0xFFFFFFFF means centre.
        var raw = degrees is null
            ? 0xFFFFFFFFu
            : (uint)(((int)Math.Round(degrees.Value * 100) % 36000 + 36000) % 36000);

        switch (pov)
        {
            case 1: _report.bHats = raw; break;
            case 2: _report.bHatsEx1 = raw; break;
            case 3: _report.bHatsEx2 = raw; break;
            case 4: _report.bHatsEx3 = raw; break;
        }
    }

    /// <summary>
    /// Discrete POV position: 0 = north, 1 = east, 2 = south, 3 = west, null for centred.
    /// Shares the same report fields as the continuous hats - the driver interprets them
    /// according to how the device was configured in vJoyConf, so a device set up for discrete
    /// hats must be driven through here and not through <see cref="SetContinuousPov"/>.
    /// </summary>
    public void SetDiscretePov(int pov, int? direction)
    {
        var raw = direction is null
            ? 0xFFFFFFFFu
            : (uint)(((direction.Value % 4) + 4) % 4);

        switch (pov)
        {
            case 1: _report.bHats = raw; break;
            case 2: _report.bHatsEx1 = raw; break;
            case 3: _report.bHatsEx2 = raw; break;
            case 4: _report.bHatsEx3 = raw; break;
        }
    }

    private void CentreAllPovs()
    {
        _report.bHats = 0xFFFFFFFF;
        _report.bHatsEx1 = 0xFFFFFFFF;
        _report.bHatsEx2 = 0xFFFFFFFF;
        _report.bHatsEx3 = 0xFFFFFFFF;
    }

    /// <summary>Parks every axis mid-range, which is what a released control should read as.</summary>
    public void CentreAllAxes()
    {
        foreach (var (axis, range) in Capabilities.Axes)
            SetAxis(axis, (int)((range.Min + range.Max) / 2));
    }

    /// <summary>Releases every button and centres everything, without dropping ownership.</summary>
    public void ClearAll()
    {
        _report.lButtons = 0;
        _report.lButtonsEx1 = 0;
        _report.lButtonsEx2 = 0;
        _report.lButtonsEx3 = 0;
        CentreAllPovs();
        CentreAllAxes();
    }

    /// <summary>Pushes the accumulated report to the driver.</summary>
    public bool Flush()
    {
        if (!_acquired) return false;
        var ok = VJoyInterop.UpdateVJD(DeviceId, ref _report);
        if (!ok) UpdateFailures++;
        return ok;
    }

    public void Dispose()
    {
        if (!_acquired) return;
        try
        {
            ClearAll();
            Flush();
            VJoyInterop.RelinquishVJD(DeviceId);
            Log.Info("vjoy", $"Released device {DeviceId}.");
        }
        catch (Exception ex)
        {
            Log.Warn("vjoy", $"Error releasing device {DeviceId}: {ex.Message}");
        }
        _acquired = false;
    }
}
