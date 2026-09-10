using System.Runtime.InteropServices;
using ModbusBridge.Core.Outputs.Uinput;
using ModbusBridge.Core.Outputs.VJoy;

namespace ModbusBridge.SmokeTest;

/// <summary>
/// The Linux counterpart to <see cref="VJoyChecks"/>, and deliberately built the same way: what
/// gets written through uinput is read back through <b>evdev</b>, a completely separate kernel
/// interface. A wrong struct layout or a missing SYN_REPORT would let every write "succeed" while
/// nothing reached a game, and only a readback notices that.
///
/// Skips on anything but Linux, and skips with a reason when /dev/uinput cannot be opened - which
/// is the usual case, because the node is root-only until a udev rule grants the input group:
///
///     KERNEL=="uinput", MODE="0660", GROUP="input", OPTIONS+="static_node=uinput"
/// </summary>
internal static class UinputChecks
{
    public static void Run(Action<bool, string> check, Action<string> section, Action<string> note)
    {
        section("uinput virtual gamepad");

        if (!OperatingSystem.IsLinux())
        {
            note("SKIPPED - uinput is Linux-only.");
            return;
        }

        if (!UinputDevice.IsSupported)
        {
            note("SKIPPED - /dev/uinput is absent. Load the module: sudo modprobe uinput");
            return;
        }

        var before = ListEventNodes();

        using var device = UinputDevice.Open(99, buttonCount: 32, name: "SmokeTest Gamepad");
        if (device is null)
        {
            note($"SKIPPED - {UinputDevice.LastError}");
            return;
        }

        check(true, $"created: {device.Capabilities}");
        check(device.Capabilities.ButtonCount == 32, $"32 buttons declared (got {device.Capabilities.ButtonCount})");
        check(device.Capabilities.HasAxis(VJoyAxis.X), "X axis declared");
        check(device.Capabilities.HasAxis(VJoyAxis.Slider1), "Slider1 declared");
        check(device.Capabilities.DiscretePovCount == 1, "one discrete hat declared");
        check(device.Capabilities.Driver == "uinput", $"reports its driver ({device.Capabilities.Driver})");

        // The kernel needs a moment to publish the new node.
        Thread.Sleep(300);
        var added = ListEventNodes().Except(before).ToList();
        check(added.Count == 1, $"the kernel created exactly one input node (got {added.Count})");
        if (added.Count == 0)
        {
            note("no new /dev/input/event* node, so the readback cannot run");
            return;
        }

        var node = added[0];
        int fd;
        try
        {
            fd = OpenRead(node);
        }
        catch (Exception ex)
        {
            note($"readback SKIPPED - cannot read {node}: {ex.Message}");
            return;
        }

        if (fd < 0)
        {
            note($"readback SKIPPED - {node} is not readable (needs the input group or root)");
            return;
        }

        try
        {
            // Drain anything the kernel emitted while the device was being created, so the
            // assertions below see only what this test wrote.
            DrainEvents(fd);

            device.SetButton(3, true);
            device.SetAxis(VJoyAxis.X, 20000);
            device.SetDiscretePov(0, 1);           // east
            check(device.Flush(), "flush accepted by the kernel");

            Thread.Sleep(200);
            var events = DrainEvents(fd);

            check(events.Any(e => e.Type == 0x01 && e.Code == 0x122 && e.Value == 1),
                  $"button 3 read back as pressed through evdev ({events.Count} event(s) seen)");
            check(events.Any(e => e.Type == 0x03 && e.Code == 0x00 && e.Value == 20000),
                  "X axis read back as 20000 through evdev");
            check(events.Any(e => e.Type == 0x03 && e.Code == 0x10 && e.Value == 1),
                  "hat read back as east through evdev");
            check(events.Any(e => e.Type == 0x00),
                  "a SYN_REPORT terminated the packet - without it nothing takes effect");

            // ClearAll is what runs when driving tags go bad, and a button left pressed outlives
            // the process, so it is worth proving rather than assuming.
            device.ClearAll();
            Thread.Sleep(200);
            var cleared = DrainEvents(fd);
            check(cleared.Any(e => e.Type == 0x01 && e.Code == 0x122 && e.Value == 0),
                  "ClearAll released button 3");
        }
        finally
        {
            CloseFd(fd);
        }
    }

    private static List<string> ListEventNodes() =>
        Directory.Exists("/dev/input")
            ? Directory.GetFiles("/dev/input", "event*").OrderBy(x => x).ToList()
            : new List<string>();

    private static List<InputEvent> DrainEvents(int fd)
    {
        var found = new List<InputEvent>();
        var size = Marshal.SizeOf<InputEvent>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            // The node is opened non-blocking, so a read returning < 0 means "nothing more".
            for (var i = 0; i < 4096; i++)
            {
                var got = read(fd, buffer, size);
                if (got != size) break;
                found.Add(Marshal.PtrToStructure<InputEvent>(buffer));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return found;
    }

    private static int OpenRead(string path) => open(path, ORdOnly | ONonBlock);

    private static void CloseFd(int fd)
    {
        if (fd >= 0) close(fd);
    }

    private const int ORdOnly = 0x0000;
    private const int ONonBlock = 0x0800;

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "close")]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true, EntryPoint = "read")]
    private static extern nint read(int fd, IntPtr buffer, int count);

    [StructLayout(LayoutKind.Sequential)]
    private struct InputEvent
    {
        public nint Seconds;
        public nint Microseconds;
        public ushort Type;
        public ushort Code;
        public int Value;
    }
}
