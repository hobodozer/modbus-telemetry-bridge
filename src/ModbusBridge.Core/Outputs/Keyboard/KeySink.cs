using System.Runtime.InteropServices;
using ModbusBridge.Core.Diagnostics;

namespace ModbusBridge.Core.Outputs.Keyboard;

/// <summary>
/// Where keystrokes go. An interface because typing into the desktop is not something a test - or
/// an unattended session - should ever do by accident; the dry-run sink is the default.
/// </summary>
public interface IKeySink
{
    void Down(Keystroke key);
    void Up(Keystroke key);
}

/// <summary>Records what would have been sent, and sends nothing. The default.</summary>
public sealed class DryRunKeySink : IKeySink
{
    private readonly bool _log;

    public List<string> Sent { get; } = new();

    public DryRunKeySink(bool log = true) => _log = log;

    public void Down(Keystroke key) => Record("down", key);
    public void Up(Keystroke key) => Record("up", key);

    private void Record(string action, Keystroke key)
    {
        // The key's own name, not its ToString: modifiers are sent as separate keystrokes, so
        // including them here would report "down ctrl" followed by "down ctrl+s".
        var text = $"{action} {key.Name}";
        lock (Sent)
        {
            Sent.Add(text);
            // Bounded, so a long unattended run cannot grow this without limit.
            if (Sent.Count > 500) Sent.RemoveAt(0);
        }
        if (_log) Log.Info("keyboard", $"[dry run] {text}");
    }
}

/// <summary>
/// Sends real keystrokes through SendInput.
///
/// Uses scan codes rather than virtual keys: many games read the keyboard through DirectInput,
/// which looks at scan codes and ignores virtual-key-only injection entirely.
/// </summary>
public sealed class SendInputKeySink : IKeySink
{
    private static bool _warnedUnsupported;

    public void Down(Keystroke key) => Send(key, false);
    public void Up(Keystroke key) => Send(key, true);

    private static void Send(Keystroke key, bool up)
    {
        // Modifiers go down before the key and up after it, so the order is handled by the caller
        // pressing/releasing them as separate keystrokes.
        SendScan(key.VirtualKey, up);
    }

    internal static void SendScan(ushort virtualKey, bool up)
    {
        // SendInput is user32. There is no cross-platform equivalent, so off Windows this sink
        // does nothing rather than throwing - the dry-run sink is the default anyway, and a
        // keyboard mapping that silently does nothing beats a bridge that will not start.
        if (!OperatingSystem.IsWindows())
        {
            if (!_warnedUnsupported)
            {
                _warnedUnsupported = true;
                Log.Warn("keyboard", "Keyboard output needs Windows (user32 SendInput); nothing is sent.");
            }
            return;
        }

        var scan = (ushort)MapVirtualKey(virtualKey, 0);
        var flags = KeyeventfScancode | (up ? KeyeventfKeyup : 0u);

        // Extended keys (arrows, insert/delete/home/end, right ctrl/alt) need the extended flag or
        // they land as their numeric-keypad equivalents.
        if (IsExtended(virtualKey)) flags |= KeyeventfExtendedkey;

        var input = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = 0,
                    Scan = scan,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    private static bool IsExtended(ushort vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or
        0x2D or 0x2E or 0x2C or 0x90 or 0xA3 or 0xA5;

    private const uint InputKeyboard = 1;
    private const uint KeyeventfExtendedkey = 0x0001;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfScancode = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        // The union is sized by the largest member (MOUSEINPUT); padding it keeps the struct the
        // size SendInput expects on x64.
        [FieldOffset(0)] public ulong Padding0;
        [FieldOffset(8)] public ulong Padding1;
        [FieldOffset(16)] public ulong Padding2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
