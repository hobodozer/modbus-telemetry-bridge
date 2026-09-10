using System.Runtime.InteropServices;

namespace ModbusBridge.Core.Outputs.VJoy;

/// <summary>vJoy axis, identified by its HID usage code.</summary>
public enum VJoyAxis
{
    X = 0x30,
    Y = 0x31,
    Z = 0x32,
    RX = 0x33,
    RY = 0x34,
    RZ = 0x35,
    Slider0 = 0x36,
    Slider1 = 0x37
}

/// <summary>Ownership state of a vJoy device.</summary>
public enum VjdStatus
{
    Own = 0,
    Free = 1,
    Busy = 2,
    Missing = 3,
    Unknown = 4
}

/// <summary>
/// The report structure handed to <c>UpdateVJD</c>. Field order and types mirror
/// JOYSTICK_POSITION_V2 in vJoy's public.h exactly — a mismatch here silently scrambles axes and
/// buttons rather than failing, so the smoke test reads the device back to prove the layout.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct JoystickPositionV2
{
    public byte bDevice;

    public int wThrottle;
    public int wRudder;
    public int wAileron;
    public int wAxisX;
    public int wAxisY;
    public int wAxisZ;
    public int wAxisXRot;
    public int wAxisYRot;
    public int wAxisZRot;
    public int wSlider;
    public int wDial;
    public int wWheel;
    public int wAxisVX;
    public int wAxisVY;
    public int wAxisVZ;
    public int wAxisVBRX;
    public int wAxisVBRY;
    public int wAxisVBRZ;

    /// <summary>Buttons 1-32, one bit each.</summary>
    public uint lButtons;

    /// <summary>Continuous hat switches, 0..35999 in hundredths of a degree; -1 (0xFFFFFFFF) is centre.</summary>
    public uint bHats;
    public uint bHatsEx1;
    public uint bHatsEx2;
    public uint bHatsEx3;

    /// <summary>Buttons 33-64.</summary>
    public uint lButtonsEx1;
    /// <summary>Buttons 65-96.</summary>
    public uint lButtonsEx2;
    /// <summary>Buttons 97-128.</summary>
    public uint lButtonsEx3;
}

/// <summary>
/// P/Invoke surface for vJoyInterface.dll.
/// <para>
/// The DLL is resolved at runtime rather than bound at load time, so the bridge still starts on a
/// machine with no vJoy installed — <see cref="IsAvailable"/> simply reports false and the feeder
/// stays offline instead of the process failing to launch.
/// </para>
/// </summary>
public static class VJoyInterop
{
    private const string Dll = "vJoyInterface.dll";

    private static bool _probed;
    private static bool _available;
    private static string? _loadError;

    /// <summary>Standard install locations, tried in order before falling back to the search path.</summary>
    private static readonly string[] ProbePaths =
    {
        @"C:\Program Files\vJoy\x64",
        @"C:\Program Files\vJoy\x86",
        @"C:\Program Files (x86)\vJoy\x64",
        @"C:\Program Files (x86)\vJoy\x86"
    };

    static VJoyInterop()
    {
        NativeLibrary.SetDllImportResolver(typeof(VJoyInterop).Assembly, Resolve);
    }

    private static nint Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? path)
    {
        if (!string.Equals(libraryName, Dll, StringComparison.OrdinalIgnoreCase)) return nint.Zero;

        foreach (var directory in ProbePaths)
        {
            var candidate = Path.Combine(directory, Dll);
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        // Fall back to the normal search order (app directory, PATH).
        return NativeLibrary.TryLoad(Dll, out var fallback) ? fallback : nint.Zero;
    }

    /// <summary>True when vJoyInterface.dll could be loaded and the driver reports enabled.</summary>
    public static bool IsAvailable
    {
        get
        {
            Probe();
            return _available;
        }
    }

    /// <summary>Why <see cref="IsAvailable"/> is false, for the UI and log.</summary>
    public static string? LoadError
    {
        get
        {
            Probe();
            return _loadError;
        }
    }

    private static void Probe()
    {
        if (_probed) return;
        _probed = true;

        try
        {
            if (!vJoyEnabled())
            {
                _loadError = "vJoyInterface.dll loaded but the vJoy driver reports disabled. " +
                             "Check that the vJoy driver is installed and enabled in Device Manager.";
                return;
            }
            _available = true;
        }
        catch (DllNotFoundException)
        {
            _loadError = "vJoyInterface.dll not found. Install vJoy, or place the DLL beside the bridge executable.";
        }
        catch (BadImageFormatException)
        {
            _loadError = "vJoyInterface.dll is the wrong architecture. The bridge is 64-bit and needs the x64 vJoy DLL.";
        }
        catch (Exception ex)
        {
            _loadError = $"vJoy could not be initialised: {ex.GetType().Name} {ex.Message}";
        }
    }

    /// <summary>Re-runs the availability probe, e.g. after the user installs the driver.</summary>
    public static void ResetProbe()
    {
        _probed = false;
        _available = false;
        _loadError = null;
    }

    // ---- General driver ---------------------------------------------------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool vJoyEnabled();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern short GetvJoyVersion();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint GetvJoyProductString();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint GetvJoyManufacturerString();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool DriverMatch(ref ushort dllVersion, ref ushort driverVersion);

    // ---- Device ownership -------------------------------------------------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern VjdStatus GetVJDStatus(uint deviceId);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool AcquireVJD(uint deviceId);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void RelinquishVJD(uint deviceId);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ResetVJD(uint deviceId);

    // ---- Capabilities -----------------------------------------------------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetVJDButtonNumber(uint deviceId);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetVJDDiscPovNumber(uint deviceId);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetVJDContPovNumber(uint deviceId);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetVJDAxisExist(uint deviceId, uint axis);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetVJDAxisMax(uint deviceId, uint axis, ref long max);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetVJDAxisMin(uint deviceId, uint axis, ref long min);

    // ---- Feeding ----------------------------------------------------------------------------

    /// <summary>Pushes a complete report in one driver call — far cheaper than per-control setters.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool UpdateVJD(uint deviceId, ref JoystickPositionV2 report);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetAxis(int value, uint deviceId, uint axis);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetBtn(bool value, uint deviceId, byte button);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetContPov(int value, uint deviceId, byte pov);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetDiscPov(int value, uint deviceId, byte pov);

    public static string ProductString() => Marshal.PtrToStringUni(GetvJoyProductString()) ?? "vJoy";
    public static string ManufacturerString() => Marshal.PtrToStringUni(GetvJoyManufacturerString()) ?? "";
}
