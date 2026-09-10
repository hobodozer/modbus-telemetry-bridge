using System.Text;

namespace ModbusBridge.Core.Outputs.Keyboard;

/// <summary>One keystroke: a key plus the modifiers held with it.</summary>
public readonly record struct Keystroke(ushort VirtualKey, string Name, bool Ctrl, bool Alt, bool Shift, bool Win)
{
    public override string ToString()
    {
        var text = new StringBuilder();
        if (Ctrl) text.Append("ctrl+");
        if (Alt) text.Append("alt+");
        if (Shift) text.Append("shift+");
        if (Win) text.Append("win+");
        text.Append(Name);
        return text.ToString();
    }
}

/// <summary>A step in a macro: either a keystroke or a pause.</summary>
public readonly record struct MacroStep(Keystroke? Key, int DelayMs)
{
    public override string ToString() => Key is { } k ? k.ToString() : $"wait {DelayMs}";
}

/// <summary>
/// Parses key definitions written the way people say them: "f1", "ctrl+shift+p",
/// "a, b, wait 250, enter". Kept separate from the sending so it can be tested without a desktop.
/// </summary>
public static class KeySpec
{
    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["backspace"] = 0x08, ["tab"] = 0x09, ["enter"] = 0x0D, ["return"] = 0x0D,
        ["esc"] = 0x1B, ["escape"] = 0x1B, ["space"] = 0x20,
        ["pageup"] = 0x21, ["pagedown"] = 0x22, ["end"] = 0x23, ["home"] = 0x24,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["insert"] = 0x2D, ["delete"] = 0x2E, ["del"] = 0x2E,
        ["numlock"] = 0x90, ["scrolllock"] = 0x91, ["capslock"] = 0x14,
        ["printscreen"] = 0x2C, ["pause"] = 0x13,
        ["minus"] = 0xBD, ["equals"] = 0xBB, ["comma"] = 0xBC, ["period"] = 0xBE,
        ["slash"] = 0xBF, ["backslash"] = 0xDC, ["semicolon"] = 0xBA, ["apostrophe"] = 0xDE,
        ["leftbracket"] = 0xDB, ["rightbracket"] = 0xDD, ["grave"] = 0xC0,
        ["numpad0"] = 0x60, ["numpad1"] = 0x61, ["numpad2"] = 0x62, ["numpad3"] = 0x63,
        ["numpad4"] = 0x64, ["numpad5"] = 0x65, ["numpad6"] = 0x66, ["numpad7"] = 0x67,
        ["numpad8"] = 0x68, ["numpad9"] = 0x69,
        ["multiply"] = 0x6A, ["add"] = 0x6B, ["subtract"] = 0x6D, ["decimal"] = 0x6E, ["divide"] = 0x6F
    };

    /// <summary>Parses one keystroke such as "ctrl+alt+f4". Throws with a usable message if it cannot.</summary>
    public static Keystroke ParseKey(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Empty key.");

        bool ctrl = false, alt = false, shift = false, win = false;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new FormatException($"'{text}' is not a key.");

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win" or "windows" or "meta": win = true; break;
                default: throw new FormatException($"'{parts[i]}' is not a modifier.");
            }
        }

        var key = parts[^1];
        var vk = Resolve(key);
        return new Keystroke(vk, key.ToLowerInvariant(), ctrl, alt, shift, win);
    }

    private static ushort Resolve(string key)
    {
        if (Named.TryGetValue(key, out var named)) return named;

        // Function keys F1-F24 are contiguous from 0x70.
        if (key.Length >= 2 && (key[0] is 'f' or 'F') && int.TryParse(key[1..], out var number)
            && number is >= 1 and <= 24)
            return (ushort)(0x6F + number);

        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
        }

        throw new FormatException($"'{key}' is not a known key.");
    }

    /// <summary>
    /// Parses a whole sequence: comma-separated keystrokes, with "wait &lt;ms&gt;" for a pause.
    /// A single key is just a one-step sequence, so Hold and Tap use the same parser.
    /// </summary>
    public static List<MacroStep> ParseSequence(string text)
    {
        var steps = new List<MacroStep>();
        if (string.IsNullOrWhiteSpace(text)) return steps;

        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("wait", StringComparison.OrdinalIgnoreCase))
            {
                var argument = raw[4..].Trim();
                if (!int.TryParse(argument, out var ms) || ms < 0)
                    throw new FormatException($"'{raw}' should be 'wait <milliseconds>'.");
                steps.Add(new MacroStep(null, ms));
                continue;
            }

            steps.Add(new MacroStep(ParseKey(raw), 0));
        }

        return steps;
    }

    public static bool TryParseSequence(string text, out List<MacroStep> steps, out string? error)
    {
        try
        {
            steps = ParseSequence(text);
            error = steps.Count == 0 ? "no keys" : null;
            return error is null;
        }
        catch (Exception ex)
        {
            steps = new List<MacroStep>();
            error = ex.Message;
            return false;
        }
    }
}
