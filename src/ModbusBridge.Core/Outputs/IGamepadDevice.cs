using ModbusBridge.Core.Outputs.VJoy;

namespace ModbusBridge.Core.Outputs;

/// <summary>
/// What one virtual gamepad actually provides, read from the driver at open time.
///
/// Was <c>VJoyCapabilities</c>. The shape did not change when uinput arrived - a device has
/// buttons, axes with raw ranges, and hats either way - only the name was wrong.
/// </summary>
public sealed class GamepadCapabilities
{
    public required uint DeviceId { get; init; }
    public required int ButtonCount { get; init; }
    public required int ContinuousPovCount { get; init; }
    public required int DiscretePovCount { get; init; }

    /// <summary>Axes the device exposes, with the raw range the driver expects.</summary>
    public required IReadOnlyDictionary<VJoyAxis, (long Min, long Max)> Axes { get; init; }

    /// <summary>Which backend produced this - "vJoy" or "uinput". Shown in logs and the UI.</summary>
    public string Driver { get; init; } = "vJoy";

    public bool HasAxis(VJoyAxis axis) => Axes.ContainsKey(axis);

    public override string ToString() =>
        $"{Driver} device {DeviceId}: {ButtonCount} button(s), " +
        $"{Axes.Count} axis/axes ({string.Join(", ", Axes.Keys)}), " +
        $"{ContinuousPovCount} continuous + {DiscretePovCount} discrete POV(s)";
}

/// <summary>
/// A virtual gamepad the feeder writes to.
///
/// vJoy on Windows and uinput on Linux are entirely different mechanisms - a signed kernel driver
/// with a C API, versus an ioctl-configured character device - but the surface the feeder needs is
/// ten methods wide. Behind this interface the whole of <see cref="VJoy.VJoyFeeder"/> works on
/// both: shift layers, per-game profiles, hat composition, and the release logic that lifts every
/// control when its driving tags go bad. That logic is where the bugs have been, and it should
/// exist once rather than twice.
///
/// Axis names stay the HID ones (<see cref="VJoyAxis"/>). Linux calls them ABS_X, ABS_Y and so on;
/// that translation belongs in the uinput device, not in config files people have already written.
/// </summary>
public interface IGamepadDevice : IDisposable
{
    GamepadCapabilities Capabilities { get; }

    /// <summary>Buttons are 1-based, the way a game numbers them for the user.</summary>
    void SetButton(int button, bool pressed);

    bool GetButton(int button);

    void SetAxis(VJoyAxis axis, int value);

    int GetAxis(VJoyAxis axis);

    /// <summary>Converts 0..1 into this driver's raw range for that axis.</summary>
    int ScaleToAxis(VJoyAxis axis, double normalised);

    /// <summary>Degrees clockwise from north, or null for centred.</summary>
    void SetContinuousPov(int pov, double? degrees);

    /// <summary>0=N, 1=E, 2=S, 3=W, or null for centred.</summary>
    void SetDiscretePov(int pov, int? direction);

    void CentreAllAxes();

    /// <summary>Releases every button and centres every axis and POV.</summary>
    void ClearAll();

    /// <summary>Pushes accumulated state to the driver. False if the write failed.</summary>
    bool Flush();
}
