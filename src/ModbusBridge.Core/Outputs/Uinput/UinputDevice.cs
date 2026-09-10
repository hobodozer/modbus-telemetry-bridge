using System.Runtime.InteropServices;
using ModbusBridge.Core.Diagnostics;
using ModbusBridge.Core.Outputs.VJoy;

namespace ModbusBridge.Core.Outputs.Uinput;

/// <summary>
/// A virtual gamepad on Linux, built on the kernel's uinput interface.
///
/// This is the counterpart to <see cref="VJoyDevice"/> and the reason the Linux build is worth
/// running: without it the bridge polls a PLC and serves Modbus but cannot drive a game, which is
/// the entire point of the rig.
///
/// The mechanism is different in kind from vJoy. There is no driver to install and no API: you
/// open /dev/uinput, declare which event codes the device will emit via ioctl, write a
/// uinput_setup struct, and the kernel creates a real input device that games see through evdev.
/// After that, state is pushed as a stream of input_event writes terminated by SYN_REPORT.
///
/// Permissions are the one operational wrinkle. /dev/uinput is root-only by default, so either
/// grant the user access with a udev rule:
///
///     KERNEL=="uinput", MODE="0660", GROUP="input", OPTIONS+="static_node=uinput"
///
/// (then add the user to the input group), or run the bridge as root. The device reports itself
/// unavailable rather than throwing when it cannot open, so a machine without the permission still
/// runs everything else.
/// </summary>
public sealed class UinputDevice : IGamepadDevice
{
    // ---- uinput and evdev constants, from linux/uinput.h and linux/input-event-codes.h ------
    private const int UinputMaxNameSize = 80;

    private const ushort EvSyn = 0x00;
    private const ushort EvKey = 0x01;
    private const ushort EvAbs = 0x03;
    private const ushort SynReport = 0;

    // BTN_JOYSTICK..BTN_DEAD is 0x120..0x12F, then BTN_TRIGGER_HAPPY is 0x2C0..0x2FF. Together
    // that is 80 buttons, which covers the rig's 60 with room to spare.
    private const ushort BtnJoystick = 0x120;
    private const ushort BtnTriggerHappy = 0x2C0;
    private const int LowButtonCount = 16;
    private const int HighButtonCount = 64;

    private const ushort AbsHat0X = 0x10;
    private const ushort AbsHat0Y = 0x11;

    private const int UiSetEvBit = 0x40045564;    // _IOW(UINPUT_IOCTL_BASE, 100, int)
    private const int UiSetKeyBit = 0x40045565;   // 101
    private const int UiSetAbsBit = 0x40045567;   // 103
    private const int UiDevCreate = 0x5501;
    private const int UiDevDestroy = 0x5502;
    private const int UiDevSetup = 0x405c5503;    // _IOW(UINPUT_IOCTL_BASE, 3, uinput_setup)

    private const int AbsMin = 0;
    private const int AbsMax = 32767;
    private const int AbsCentre = 16384;

    /// <summary>HID axis names mapped to evdev ABS codes, so config files stay driver-neutral.</summary>
    private static readonly Dictionary<VJoyAxis, ushort> AxisCodes = new()
    {
        [VJoyAxis.X] = 0x00,        // ABS_X
        [VJoyAxis.Y] = 0x01,        // ABS_Y
        [VJoyAxis.Z] = 0x02,        // ABS_Z
        [VJoyAxis.RX] = 0x03,       // ABS_RX
        [VJoyAxis.RY] = 0x04,       // ABS_RY
        [VJoyAxis.RZ] = 0x05,       // ABS_RZ
        [VJoyAxis.Slider0] = 0x06,  // ABS_THROTTLE
        [VJoyAxis.Slider1] = 0x07,  // ABS_RUDDER
    };

    private int _fd = -1;
    private readonly bool[] _buttons;
    private readonly Dictionary<VJoyAxis, int> _axes = new();
    private int _hatX, _hatY;
    private bool _dirty;

    public GamepadCapabilities Capabilities { get; }
    public long UpdateFailures { get; private set; }

    private UinputDevice(int fd, uint deviceId, int buttonCount)
    {
        _fd = fd;
        _buttons = new bool[buttonCount + 1];   // 1-based, index 0 unused
        foreach (var axis in AxisCodes.Keys) _axes[axis] = AbsCentre;

        Capabilities = new GamepadCapabilities
        {
            DeviceId = deviceId,
            ButtonCount = buttonCount,
            // uinput hats are a pair of -1/0/+1 axes, which is a discrete 8-way hat. There is no
            // continuous equivalent, so the feeder's discrete path is the one that applies.
            ContinuousPovCount = 0,
            DiscretePovCount = 1,
            Axes = AxisCodes.Keys.ToDictionary(a => a, _ => ((long)AbsMin, (long)AbsMax)),
            Driver = "uinput"
        };
    }

    public static bool IsSupported => OperatingSystem.IsLinux() && File.Exists("/dev/uinput");

    /// <summary>Why the device could not be opened, for the status display.</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// Creates the virtual device. Returns null and sets <see cref="LastError"/> rather than
    /// throwing: a machine without permission on /dev/uinput must still run the rest of the bridge.
    /// </summary>
    public static UinputDevice? Open(uint deviceId, int buttonCount = 60, string name = "Modbus Telemetry Bridge")
    {
        if (!OperatingSystem.IsLinux())
        {
            LastError = "uinput is Linux-only.";
            return null;
        }
        if (!File.Exists("/dev/uinput"))
        {
            LastError = "/dev/uinput does not exist. Load the uinput module: sudo modprobe uinput";
            return null;
        }

        var fd = open("/dev/uinput", OWrOnly | ONonBlock);
        if (fd < 0)
        {
            LastError = "Cannot open /dev/uinput (permission denied?). Add a udev rule granting the " +
                        "input group access, or run as root.";
            return null;
        }

        try
        {
            buttonCount = Math.Clamp(buttonCount, 1, LowButtonCount + HighButtonCount);

            Check(ioctl(fd, UiSetEvBit, EvKey), "enable key events");
            for (var i = 1; i <= buttonCount; i++)
                Check(ioctl(fd, UiSetKeyBit, ButtonCode(i)), $"enable button {i}");

            Check(ioctl(fd, UiSetEvBit, EvAbs), "enable absolute axes");
            foreach (var code in AxisCodes.Values)
                Check(ioctl(fd, UiSetAbsBit, code), "enable axis");
            Check(ioctl(fd, UiSetAbsBit, AbsHat0X), "enable hat X");
            Check(ioctl(fd, UiSetAbsBit, AbsHat0Y), "enable hat Y");

            var setup = new UinputSetup
            {
                Id = new InputId { BusType = 0x03, Vendor = 0x1209, Product = 0x4D42, Version = 1 },
                FfEffectsMax = 0
            };
            var bytes = System.Text.Encoding.ASCII.GetBytes(name);
            setup.Name = new byte[UinputMaxNameSize];
            Array.Copy(bytes, setup.Name, Math.Min(bytes.Length, UinputMaxNameSize - 1));

            // The absolute axis ranges have to be declared before UI_DEV_CREATE, through
            // UI_ABS_SETUP or the legacy uinput_user_dev write. UI_DEV_SETUP plus per-axis
            // UI_ABS_SETUP is the modern path; the ranges here match vJoy's 0..32767 so a config
            // written for one driver produces the same values on the other.
            SetupAbs(fd, setup, AxisCodes.Values, AbsMin, AbsMax);
            SetupAbs(fd, setup, new ushort[] { AbsHat0X, AbsHat0Y }, -1, 1);

            Check(ioctl(fd, UiDevCreate, 0), "create the device");

            Log.Info("uinput", $"Created virtual gamepad '{name}': {buttonCount} button(s), " +
                               $"{AxisCodes.Count} axis/axes, 1 discrete hat.");
            return new UinputDevice(fd, deviceId, buttonCount);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            close(fd);
            return null;
        }
    }

    private static void SetupAbs(int fd, UinputSetup setup, IEnumerable<ushort> codes, int min, int max)
    {
        foreach (var code in codes)
        {
            var abs = new UinputAbsSetup
            {
                Code = code,
                AbsInfo = new AbsInfo { Minimum = min, Maximum = max, Flat = 0, Fuzz = 0 }
            };
            var size = Marshal.SizeOf<UinputAbsSetup>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(abs, buffer, false);
                // UI_ABS_SETUP = _IOW(UINPUT_IOCTL_BASE, 4, struct uinput_abs_setup)
                var request = 0x40000000 | (size << 16) | ('U' << 8) | 4;
                Check(ioctl(fd, request, buffer), $"set range for axis {code}");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        var setupSize = Marshal.SizeOf<UinputSetup>();
        var setupBuffer = Marshal.AllocHGlobal(setupSize);
        try
        {
            Marshal.StructureToPtr(setup, setupBuffer, false);
            Check(ioctl(fd, UiDevSetup, setupBuffer), "device setup");
        }
        finally
        {
            Marshal.FreeHGlobal(setupBuffer);
        }
    }

    private static void Check(int result, string what)
    {
        if (result < 0) throw new IOException($"uinput: could not {what} (errno {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>Button 1..16 map to BTN_JOYSTICK.., above that to BTN_TRIGGER_HAPPY..</summary>
    private static ushort ButtonCode(int button) =>
        button <= LowButtonCount
            ? (ushort)(BtnJoystick + button - 1)
            : (ushort)(BtnTriggerHappy + button - LowButtonCount - 1);

    // ---- IGamepadDevice --------------------------------------------------------------------

    public void SetButton(int button, bool pressed)
    {
        if (button < 1 || button >= _buttons.Length) return;
        if (_buttons[button] == pressed) return;
        _buttons[button] = pressed;
        _dirty = true;
    }

    public bool GetButton(int button) =>
        button >= 1 && button < _buttons.Length && _buttons[button];

    public void SetAxis(VJoyAxis axis, int value)
    {
        if (!_axes.ContainsKey(axis)) return;
        var clamped = Math.Clamp(value, AbsMin, AbsMax);
        if (_axes[axis] == clamped) return;
        _axes[axis] = clamped;
        _dirty = true;
    }

    public int GetAxis(VJoyAxis axis) => _axes.TryGetValue(axis, out var v) ? v : AbsCentre;

    public int ScaleToAxis(VJoyAxis axis, double normalised) =>
        (int)Math.Round(Math.Clamp(normalised, 0d, 1d) * (AbsMax - AbsMin)) + AbsMin;

    public void SetContinuousPov(int pov, double? degrees)
    {
        // No continuous hat exists on uinput, so a continuous request is rounded to the nearest
        // of the eight directions the hat pair can express. Silently ignoring it would leave a
        // configured hat dead with no explanation.
        if (degrees is not { } value)
        {
            SetHat(0, 0);
            return;
        }
        var octant = ((int)Math.Round(value / 45.0)) % 8;
        if (octant < 0) octant += 8;
        int[] xs = { 0, 1, 1, 1, 0, -1, -1, -1 };
        int[] ys = { -1, -1, 0, 1, 1, 1, 0, -1 };
        SetHat(xs[octant], ys[octant]);
    }

    public void SetDiscretePov(int pov, int? direction)
    {
        switch (direction)
        {
            case 0: SetHat(0, -1); break;   // north
            case 1: SetHat(1, 0); break;    // east
            case 2: SetHat(0, 1); break;    // south
            case 3: SetHat(-1, 0); break;   // west
            default: SetHat(0, 0); break;
        }
    }

    private void SetHat(int x, int y)
    {
        if (_hatX == x && _hatY == y) return;
        _hatX = x;
        _hatY = y;
        _dirty = true;
    }

    public void CentreAllAxes()
    {
        foreach (var axis in _axes.Keys.ToList()) SetAxis(axis, AbsCentre);
    }

    public void ClearAll()
    {
        for (var i = 1; i < _buttons.Length; i++) SetButton(i, false);
        CentreAllAxes();
        SetHat(0, 0);
        Flush();
    }

    public bool Flush()
    {
        if (_fd < 0) return false;
        if (!_dirty) return true;

        try
        {
            for (var i = 1; i < _buttons.Length; i++)
                Emit(EvKey, ButtonCode(i), _buttons[i] ? 1 : 0);
            foreach (var (axis, value) in _axes)
                Emit(EvAbs, AxisCodes[axis], value);
            Emit(EvAbs, AbsHat0X, _hatX);
            Emit(EvAbs, AbsHat0Y, _hatY);
            Emit(EvSyn, SynReport, 0);   // nothing takes effect until the report is synced
            _dirty = false;
            return true;
        }
        catch (Exception ex)
        {
            UpdateFailures++;
            if (UpdateFailures == 1) Log.Warn("uinput", $"Write failed: {ex.Message}");
            return false;
        }
    }

    private void Emit(ushort type, ushort code, int value)
    {
        var ev = new InputEvent { Type = type, Code = code, Value = value };
        var size = Marshal.SizeOf<InputEvent>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(ev, buffer, false);
            if (write(_fd, buffer, size) != size)
                throw new IOException($"short write (errno {Marshal.GetLastWin32Error()})");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_fd < 0) return;
        try
        {
            // Release everything before the device disappears. A button left pressed outlives the
            // process, exactly as it does with vJoy.
            ClearAll();
            ioctl(_fd, UiDevDestroy, 0);
        }
        catch
        {
            // Nothing useful to do while tearing down.
        }
        close(_fd);
        _fd = -1;
        Log.Info("uinput", "Virtual gamepad removed.");
    }

    // ---- interop ---------------------------------------------------------------------------

    private const int OWrOnly = 0x0001;
    private const int ONonBlock = 0x0800;

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "close")]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true, EntryPoint = "write")]
    private static extern nint write(int fd, IntPtr buffer, int count);

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static extern int ioctl(int fd, int request, int value);

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static extern int ioctl(int fd, int request, IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    private struct InputEvent
    {
        // struct timeval, which the kernel fills in; zero is accepted on write.
        public nint Seconds;
        public nint Microseconds;
        public ushort Type;
        public ushort Code;
        public int Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputId
    {
        public ushort BusType;
        public ushort Vendor;
        public ushort Product;
        public ushort Version;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UinputSetup
    {
        public InputId Id;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = UinputMaxNameSize)]
        public byte[] Name;
        public uint FfEffectsMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AbsInfo
    {
        public int Value;
        public int Minimum;
        public int Maximum;
        public int Fuzz;
        public int Flat;
        public int Resolution;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UinputAbsSetup
    {
        public ushort Code;
        public AbsInfo AbsInfo;
    }
}
