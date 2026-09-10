using System.Runtime.InteropServices;

namespace ModbusBridge.SmokeTest;

/// <summary>
/// Reads a virtual joystick back through the legacy winmm joystick API. This is deliberately a
/// completely different code path from the one that writes it: if the JOYSTICK_POSITION_V2 struct
/// layout were wrong, the write would still "succeed" and only a readback would notice.
/// winmm exposes axes and the first 32 buttons, which is enough to prove the layout.
/// </summary>
internal static class JoystickReader
{
    private const int MaxPNameLen = 32;
    private const int MaxOemVxdName = 260;
    private const uint JoyReturnAll = 0x000000FF;
    private const uint JoyErrNoError = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JoyCaps
    {
        public ushort wMid;
        public ushort wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPNameLen)]
        public string szPname;
        public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax;
        public uint wNumButtons, wPeriodMin, wPeriodMax;
        public uint wRmin, wRmax, wUmin, wUmax, wVmin, wVmax;
        public uint wCaps, wMaxAxes, wNumAxes, wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPNameLen)]
        public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxOemVxdName)]
        public string szOEMVxD;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyInfoEx
    {
        public uint dwSize;
        public uint dwFlags;
        public uint dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos;
        public uint dwButtons;
        public uint dwButtonNumber;
        public uint dwPOV;
        public uint dwReserved1, dwReserved2;
    }

    [DllImport("winmm.dll")] private static extern uint joyGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "joyGetDevCapsW")]
    private static extern uint joyGetDevCaps(nuint joyId, ref JoyCaps caps, uint size);
    [DllImport("winmm.dll")] private static extern uint joyGetPosEx(uint joyId, ref JoyInfoEx info);

    public sealed record Reading(
        uint XPos, uint YPos, uint ZPos, uint RPos, uint UPos, uint VPos,
        uint Buttons, uint Pov);

    public sealed record Device(uint Index, string Name, uint NumButtons, uint NumAxes,
                                uint XMin, uint XMax, uint YMin, uint YMax, uint ZMin, uint ZMax);

    /// <summary>
    /// Every joystick slot that currently responds. winmm's product name comes from an optional
    /// registry key and is usually a generic string like "Microsoft PC-joystick driver", so callers
    /// must identify the device they want by behaviour, not by name.
    /// </summary>
    public static IReadOnlyList<Device> ListLive()
    {
        var devices = new List<Device>();
        var count = joyGetNumDevs();

        for (uint i = 0; i < count; i++)
        {
            var caps = new JoyCaps();
            if (joyGetDevCaps(i, ref caps, (uint)Marshal.SizeOf<JoyCaps>()) != JoyErrNoError) continue;

            // A configured-but-absent slot still returns caps; a position read proves it is live.
            var info = new JoyInfoEx { dwSize = (uint)Marshal.SizeOf<JoyInfoEx>(), dwFlags = JoyReturnAll };
            if (joyGetPosEx(i, ref info) != JoyErrNoError) continue;

            devices.Add(new Device(i, string.IsNullOrWhiteSpace(caps.szPname) ? "(unnamed)" : caps.szPname,
                                   caps.wNumButtons, caps.wNumAxes,
                                   caps.wXmin, caps.wXmax, caps.wYmin, caps.wYmax, caps.wZmin, caps.wZmax));
        }

        return devices;
    }

    /// <summary>
    /// Identifies which live joystick is the one being fed, by toggling a control and seeing which
    /// device's reading moves. Far more reliable than string-matching a product name, and it proves
    /// the write path reaches a real HID device at the same time.
    /// </summary>
    public static Device? IdentifyByProbe(IReadOnlyList<Device> candidates,
                                          Action pressProbe, Action releaseProbe,
                                          int settleMs = 60)
    {
        releaseProbe();
        Thread.Sleep(settleMs);
        var baseline = candidates.ToDictionary(d => d.Index, d => Read(d.Index));

        pressProbe();
        Thread.Sleep(settleMs);

        Device? found = null;
        foreach (var device in candidates)
        {
            var before = baseline[device.Index];
            var after = Read(device.Index);
            if (before is null || after is null) continue;
            if (before.Buttons != after.Buttons) { found = device; break; }
        }

        releaseProbe();
        Thread.Sleep(settleMs);
        return found;
    }

    public static Reading? Read(uint index)
    {
        var info = new JoyInfoEx { dwSize = (uint)Marshal.SizeOf<JoyInfoEx>(), dwFlags = JoyReturnAll };
        if (joyGetPosEx(index, ref info) != JoyErrNoError) return null;
        return new Reading(info.dwXpos, info.dwYpos, info.dwZpos, info.dwRpos, info.dwUpos, info.dwVpos,
                           info.dwButtons, info.dwPOV);
    }

    /// <summary>True when button <paramref name="button"/> (1-based) is set in a reading.</summary>
    public static bool ButtonPressed(Reading reading, int button) =>
        button is >= 1 and <= 32 && (reading.Buttons & 1u << button - 1) != 0;
}
