using System.Collections.ObjectModel;
using ModbusBridge.Core.Outputs.VJoy;

namespace ModbusBridge.Core.Config;

/// <summary>How a tag drives a vJoy button.</summary>
public enum VJoyButtonMode
{
    /// <summary>Button is held for exactly as long as the tag is true. The default.</summary>
    Momentary,

    /// <summary>Each rising edge flips the button. For a physical latching switch that should read as a press.</summary>
    Toggle,

    /// <summary>A rising edge emits a fixed-length press, however long the contact is actually held.</summary>
    Pulse
}

public sealed class VJoyButtonMapping
{
    public bool Enabled { get; set; } = true;

    /// <summary>Tag driving this button. Any tag works - a PLC contact, an HMI soft button, telemetry.</summary>
    public string Tag { get; set; } = "";

    /// <summary>vJoy button number, 1-based, exactly as games display it.</summary>
    public int Button { get; set; } = 1;

    public VJoyButtonMode Mode { get; set; } = VJoyButtonMode.Momentary;

    /// <summary>Press length for <see cref="VJoyButtonMode.Pulse"/>.</summary>
    public int PulseMs { get; set; } = 50;

    /// <summary>Tag value at or above which the button counts as pressed. Lets an analog drive a button.</summary>
    public double Threshold { get; set; } = 0.5;

    /// <summary>Inverts the sense - the software normally-closed toggle, at the vJoy end.</summary>
    public bool Invert { get; set; }

    /// <summary>
    /// Shift layer this mapping belongs to. Empty means the base layer, which applies whenever no
    /// layer overrides the same tag. So a panel is mapped once, and a layer only lists the buttons
    /// that change.
    /// </summary>
    public string Layer { get; set; } = "";

    /// <summary>
    /// Profile this mapping belongs to. Empty means always active; otherwise it applies only while
    /// that profile is selected, so one panel can mean different things per game.
    /// </summary>
    public string Profile { get; set; } = "";

    public string? Description { get; set; }
}

/// <summary>
/// Selects a set of mappings by what is running, so a panel does not have to be remapped by hand
/// when the game changes.
/// </summary>
public sealed class VJoyProfile
{
    public bool Enabled { get; set; } = true;

    /// <summary>Name referenced by a mapping's <c>Profile</c>. Case-insensitive.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Patterns matched against the selector tag, '*' allowed - e.g. "FarmingSimulator*".
    /// The first profile whose pattern matches wins, so order them most specific first.
    /// </summary>
    public ObservableCollection<string> Games { get; set; } = new();

    public string? Description { get; set; }
}

/// <summary>
/// A modifier contact that remaps buttons while it is held - one physical button sending different
/// vJoy buttons depending on a shift key, without doubling the panel.
/// </summary>
public sealed class VJoyShiftLayer
{
    public bool Enabled { get; set; } = true;

    /// <summary>Name referenced by <see cref="VJoyButtonMapping.Layer"/>. Case-insensitive.</summary>
    public string Name { get; set; } = "";

    /// <summary>Contact that activates the layer. Any tag: a PLC input, an HMI button, telemetry.</summary>
    public string ModifierTag { get; set; } = "";

    /// <summary>Value at or above which the modifier counts as held.</summary>
    public double Threshold { get; set; } = 0.5;

    /// <summary>Inverts the modifier, for a normally-closed shift contact.</summary>
    public bool Invert { get; set; }

    /// <summary>
    /// Highest priority wins when several modifiers are held at once, so overlapping layers
    /// resolve predictably instead of by declaration order.
    /// </summary>
    public int Priority { get; set; }

    public string? Description { get; set; }
}

public sealed class VJoyAxisMapping
{
    public bool Enabled { get; set; } = true;
    public string Tag { get; set; } = "";
    public VJoyAxis Axis { get; set; } = VJoyAxis.X;

    /// <summary>Tag values mapping to the extremes of travel. Swap them to reverse the axis.</summary>
    public double InputMin { get; set; }
    public double InputMax { get; set; } = 1.0;

    /// <summary>Fraction of travel around centre that reads as centre, 0..0.5.</summary>
    public double Deadzone { get; set; }

    /// <summary>
    /// Response curve exponent. 1 is linear; above 1 gives finer control near centre;
    /// below 1 makes it more sensitive near centre.
    /// </summary>
    public double Curve { get; set; } = 1.0;

    /// <summary>Mirrors the axis about its centre after scaling.</summary>
    public bool Invert { get; set; }

    /// <summary>Ignore movement smaller than this fraction of full travel, to reject a noisy input.</summary>
    public double Deadband { get; set; }

    /// <summary>Profile this mapping belongs to. Empty means always active.</summary>
    public string Profile { get; set; } = "";

    public string? Description { get; set; }
}

/// <summary>Where a hat's position comes from.</summary>
public enum VJoyPovSource
{
    /// <summary>A single tag holding the angle in degrees; a negative value centres the hat.</summary>
    Angle,

    /// <summary>
    /// Four separate contacts, which is how an arcade hat is actually wired - one microswitch per
    /// direction. Opposing contacts cancel to centre.
    /// </summary>
    Contacts
}

/// <summary>
/// Which kind of hat the device provides. vJoy exposes these as separate pools and a device
/// generally has one kind or the other, so the mapping has to say which it means.
/// </summary>
public enum VJoyPovKind
{
    /// <summary>Free angle in hundredths of a degree - supports diagonals.</summary>
    Continuous,

    /// <summary>Four positions only (N/E/S/W). Diagonals cannot be expressed.</summary>
    Discrete
}

public sealed class VJoyPovMapping
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Tag holding the hat angle in degrees when <see cref="Source"/> is
    /// <see cref="VJoyPovSource.Angle"/>; a negative value centres the hat. Ignored for
    /// <see cref="VJoyPovSource.Contacts"/>.
    /// </summary>
    public string Tag { get; set; } = "";

    /// <summary>Hat number within its pool, 1-4.</summary>
    public int Pov { get; set; } = 1;

    public VJoyPovSource Source { get; set; } = VJoyPovSource.Angle;

    /// <summary>
    /// Continuous or discrete. This is not cosmetic - the two are different pools in vJoy and a
    /// device configured for one has none of the other.
    /// </summary>
    public VJoyPovKind Kind { get; set; } = VJoyPovKind.Continuous;

    /// <summary>Direction contacts, used when <see cref="Source"/> is <see cref="VJoyPovSource.Contacts"/>.</summary>
    public string UpTag { get; set; } = "";
    public string RightTag { get; set; } = "";
    public string DownTag { get; set; } = "";
    public string LeftTag { get; set; } = "";

    /// <summary>Contact value at or above which a direction counts as pressed.</summary>
    public double Threshold { get; set; } = 0.5;

    /// <summary>Inverts every direction contact - normally-closed switches at the hat.</summary>
    public bool Invert { get; set; }

    /// <summary>Profile this mapping belongs to. Empty means always active.</summary>
    public string Profile { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Every direction tag that is actually set, for binding and validation.</summary>
    public IEnumerable<string> ContactTags
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(UpTag)) yield return UpTag;
            if (!string.IsNullOrWhiteSpace(RightTag)) yield return RightTag;
            if (!string.IsNullOrWhiteSpace(DownTag)) yield return DownTag;
            if (!string.IsNullOrWhiteSpace(LeftTag)) yield return LeftTag;
        }
    }

    /// <summary>True when this mapping has enough configured to drive anything.</summary>
    public bool IsUsable => Source == VJoyPovSource.Angle
        ? !string.IsNullOrWhiteSpace(Tag)
        : ContactTags.Any();
}

public sealed class VJoyDeviceConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>vJoy device id, 1-16, as configured in vJoyConf.</summary>
    public uint DeviceId { get; set; } = 1;

    /// <summary>
    /// How often the report is rebuilt and pushed. This adds to the PLC poll interval to give the
    /// total contact-to-game latency, so it is worth keeping small - a report costs about 4 us.
    /// </summary>
    public int UpdateIntervalMs { get; set; } = 5;

    /// <summary>Send a report every cycle instead of only when something changed.</summary>
    public bool AlwaysSend { get; set; }

    /// <summary>
    /// Release every button and centre every axis when the driving tags go bad (PLC offline).
    /// Leaving a button stuck down because the PLC dropped is worse than releasing it.
    /// </summary>
    public bool ReleaseOnBadQuality { get; set; } = true;

    public ObservableCollection<VJoyButtonMapping> Buttons { get; set; } = new();

    /// <summary>Shift layers. Empty means every button mapping is always active.</summary>
    public ObservableCollection<VJoyShiftLayer> Layers { get; set; } = new();

    /// <summary>Per-game profiles. Empty means every mapping is always active.</summary>
    public ObservableCollection<VJoyProfile> Profiles { get; set; } = new();

    /// <summary>
    /// Tag holding the value profiles are matched against - by default the running game, as
    /// SimHub reports it. Any tag works, so a physical selector switch can pick the profile too.
    /// </summary>
    public string ProfileTag { get; set; } = "bridge.simhubGame";
    public ObservableCollection<VJoyAxisMapping> Axes { get; set; } = new();
    public ObservableCollection<VJoyPovMapping> Povs { get; set; } = new();
}

public sealed class VJoyConfig
{
    public bool Enabled { get; set; }

    public ObservableCollection<VJoyDeviceConfig> Devices { get; set; } = new();
}

/// <summary>How a tag drives a key.</summary>
public enum KeyMode
{
    /// <summary>Held down for exactly as long as the tag is true.</summary>
    Hold,

    /// <summary>A press and release on each rising edge, optionally repeating while held.</summary>
    Tap,

    /// <summary>A sequence with waits, run once per rising edge.</summary>
    Macro
}

public sealed class KeyMapping
{
    public bool Enabled { get; set; } = true;

    /// <summary>Tag driving this key.</summary>
    public string Tag { get; set; } = "";

    /// <summary>
    /// What to send. One key for Hold ("f1", "ctrl+shift+p"); for Tap or Macro a comma-separated
    /// sequence, with "wait 250" for a pause - e.g. "ctrl+s, wait 500, enter".
    /// </summary>
    public string Keys { get; set; } = "";

    public KeyMode Mode { get; set; } = KeyMode.Tap;

    public double Threshold { get; set; } = 0.5;
    public bool Invert { get; set; }

    /// <summary>Repeat interval while the tag stays true, for Tap. 0 fires once per edge.</summary>
    public int RepeatMs { get; set; }

    public string? Description { get; set; }
}

/// <summary>
/// Keyboard output, for games that ignore joystick input for certain functions.
///
/// Types into whichever window has focus, so it defaults to a dry run that logs what it would send
/// and sends nothing. Turn <see cref="DryRun"/> off deliberately, with the game focused.
/// </summary>
public sealed class KeyboardConfig
{
    public bool Enabled { get; set; }

    /// <summary>Log keystrokes instead of sending them. On by default, on purpose.</summary>
    public bool DryRun { get; set; } = true;

    public int UpdateIntervalMs { get; set; } = 20;

    /// <summary>How long a key is held within a tap or macro step.</summary>
    public int KeyPressMs { get; set; } = 30;

    /// <summary>Gap between steps of a macro.</summary>
    public int KeyGapMs { get; set; } = 30;

    public ObservableCollection<KeyMapping> Mappings { get; set; } = new();
}
