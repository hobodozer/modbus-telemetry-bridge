using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ModbusBridge.Core.Config;
using ModbusBridge.Core.Data;
using ModbusBridge.Core.Outputs.VJoy;
using ModbusBridge.Core.Tags;

namespace ModbusBridge.App;

/// <summary>
/// Identifies a physical input by watching which tag moves, so a panel can be wired up without
/// anyone decoding a Modbus address. Press a button, name it, say what it drives.
/// </summary>
public partial class LearnInputWindow : Window
{
    /// <summary>What a learned input should end up driving.</summary>
    private enum LearnTarget { NameOnly, Button, HatDirection, Axis }

    private sealed record Candidate(string ResolvedTag, ModbusClientConfig Client, PointConfig Point, string Where);

    public sealed record Assignment(string Tag, string OldTag, string Target);

    private readonly BridgeConfig _config;
    private readonly TagBus _bus;
    private readonly DispatcherTimer _timer;

    /// <summary>Candidate tags keyed by resolved name - only points this bridge actually polls.</summary>
    private readonly Dictionary<string, Candidate> _candidates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tag VALUES as of the last arm. Deliberately not versions: TagEntry.Version counts accepted
    /// writes, not changes, and the poller writes every cycle - so every tag's version moves
    /// constantly and version-watching detects nothing useful.
    /// </summary>
    private readonly Dictionary<string, double> _baseline = new(StringComparer.OrdinalIgnoreCase);

    private readonly ObservableCollection<Assignment> _assigned = new();
    private Candidate? _captured;

    public IReadOnlyList<Assignment> Assignments => _assigned;

    public LearnInputWindow(TagBus bus, BridgeConfig config)
    {
        InitializeComponent();

        _bus = bus;
        _config = config;

        AssignedGrid.ItemsSource = _assigned;

        TargetCombo.ItemsSource = new[]
        {
            "vJoy button", "Hat direction", "Axis", "Name only"
        };
        TargetCombo.SelectedIndex = 0;

        ButtonModeCombo.ItemsSource = Enum.GetValues<VJoyButtonMode>();
        ButtonModeCombo.SelectedItem = VJoyButtonMode.Momentary;

        HatDirectionCombo.ItemsSource = new[] { "Up", "Right", "Down", "Left" };
        HatDirectionCombo.SelectedIndex = 0;

        HatKindCombo.ItemsSource = Enum.GetValues<VJoyPovKind>();
        HatKindCombo.SelectedItem = SuggestPovKind();

        AxisCombo.ItemsSource = Enum.GetValues<VJoyAxis>();
        AxisCombo.SelectedItem = VJoyAxis.X;

        BuildCandidates();
        Arm();

        // 20 ms is well inside a 10 ms poll without making the UI thread work hard.
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(20)
        };
        _timer.Tick += OnTick;
        _timer.Start();

        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>
    /// Default the hat kind to whatever the device actually provides - a device configured for
    /// discrete hats has no continuous ones, and picking the wrong one silently does nothing.
    /// </summary>
    private VJoyPovKind SuggestPovKind()
    {
        var device = _config.VJoy.Devices.FirstOrDefault();
        if (device is null) return VJoyPovKind.Continuous;

        var capabilities = VJoyDevice.Probe(device.DeviceId);
        if (capabilities is null) return VJoyPovKind.Continuous;

        return capabilities.ContinuousPovCount > 0 ? VJoyPovKind.Continuous : VJoyPovKind.Discrete;
    }

    /// <summary>
    /// Only points the bridge polls are candidates. Without this, streaming telemetry would win
    /// every race - SimHub moves hundreds of tags a second.
    /// </summary>
    private void BuildCandidates()
    {
        foreach (var client in _config.Clients.Where(c => c.Enabled))
        {
            foreach (var group in client.ReadGroups.Where(g => g.Enabled))
            {
                foreach (var point in group.Points.Where(p => p.Enabled))
                {
                    var resolved = client.TagPrefix + point.Tag;
                    _candidates[resolved] = new Candidate(resolved, client, point, group.Name);
                }
            }
        }
    }

    private void Arm()
    {
        _captured = null;
        _baseline.Clear();

        foreach (var name in _candidates.Keys)
            _baseline[name] = _bus.Find(name)?.Value.Number ?? 0;

        AssignPanel.IsEnabled = false;

        var digital = _candidates.Values.Count(c => c.Point.DataType == PointDataType.Bool);
        var analog = _candidates.Count - digital;

        StatusText.Text = _candidates.Count == 0
            ? "No polled inputs to learn from."
            : "Waiting for input...";
        DetailText.Text = _candidates.Count == 0
            ? "Enable a device with a read group first."
            : $"Watching {digital} contact(s)" +
              (IncludeAnalog ? $" and {analog} analog input(s)" : $" ({analog} analog ignored)") +
              ". Press a control on the panel.";
        UpdateCount();
    }

    private bool IncludeAnalog => IncludeAnalogBox.IsChecked == true;

    private double AnalogThreshold =>
        double.TryParse(AnalogThresholdBox.Text, out var value) && value > 0 ? value : 500;

    private void OnTick(object? sender, EventArgs e)
    {
        if (_captured is not null) return;

        foreach (var (name, candidate) in _candidates)
        {
            var entry = _bus.Find(name);
            if (entry is null) continue;

            var value = entry.Value;
            if (value.Quality <= TagQuality.Bad) continue;
            if (!_baseline.TryGetValue(name, out var since)) { _baseline[name] = value.Number; continue; }

            if (candidate.Point.DataType == PointDataType.Bool)
            {
                // Rising edge only, so the operator sees the control they are holding rather than
                // the one they just let go of. A release just re-baselines.
                if (value.Number >= 0.5 && since < 0.5)
                {
                    Capture(candidate, value);
                    return;
                }
                _baseline[name] = value.Number;
            }
            else
            {
                // Analog never sits perfectly still, so require a deliberate movement.
                if (!IncludeAnalog) continue;
                if (Math.Abs(value.Number - since) >= AnalogThreshold)
                {
                    Capture(candidate, value);
                    return;
                }
            }
        }
    }

    private void Capture(Candidate candidate, TagValue value)
    {
        _captured = candidate;

        StatusText.Text = $"Detected  {candidate.ResolvedTag}";
        DetailText.Text =
            $"group '{candidate.Where}'  offset {candidate.Point.Offset}  " +
            $"{candidate.Point.DataType}  value {value.Number:0.###}\n" +
            (candidate.Point.Description ?? "");

        PrefixText.Text = candidate.Client.TagPrefix;
        NameBox.Text = candidate.Point.Tag;
        DescriptionBox.Text = candidate.Point.Description ?? "";

        // An analog point almost certainly wants an axis; a contact almost certainly a button.
        if (candidate.Point.DataType != PointDataType.Bool)
            TargetCombo.SelectedIndex = 2;

        ButtonBox.Text = SuggestButtonNumber(candidate).ToString();

        AssignPanel.IsEnabled = true;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    /// <summary>Keeps an existing button assignment if this tag already has one; else the next free.</summary>
    private int SuggestButtonNumber(Candidate candidate)
    {
        var device = _config.VJoy.Devices.FirstOrDefault();
        if (device is null) return 1;

        var existing = device.Buttons.FirstOrDefault(b =>
            string.Equals(b.Tag, candidate.ResolvedTag, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing.Button;

        return device.Buttons.Count == 0 ? 1 : device.Buttons.Max(b => b.Button) + 1;
    }

    private LearnTarget Target => TargetCombo.SelectedIndex switch
    {
        0 => LearnTarget.Button,
        1 => LearnTarget.HatDirection,
        2 => LearnTarget.Axis,
        _ => LearnTarget.NameOnly
    };

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ButtonOptions is null) return;   // fires during InitializeComponent
        ButtonOptions.Visibility = Target == LearnTarget.Button ? Visibility.Visible : Visibility.Collapsed;
        HatOptions.Visibility = Target == LearnTarget.HatDirection ? Visibility.Visible : Visibility.Collapsed;
        AxisOptions.Visibility = Target == LearnTarget.Axis ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAssign(object sender, RoutedEventArgs e)
    {
        if (_captured is not { } candidate) return;

        var suffix = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(suffix))
        {
            MessageBox.Show("Give the input a name.", "Learn", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var oldResolved = candidate.ResolvedTag;
        var newResolved = candidate.Client.TagPrefix + suffix;

        if (!string.Equals(oldResolved, newResolved, StringComparison.OrdinalIgnoreCase)
            && _candidates.ContainsKey(newResolved))
        {
            MessageBox.Show($"'{newResolved}' is already used by another point.", "Learn",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        candidate.Point.Tag = suffix;
        candidate.Point.Description = string.IsNullOrWhiteSpace(DescriptionBox.Text)
            ? null
            : DescriptionBox.Text.Trim();

        if (!string.Equals(oldResolved, newResolved, StringComparison.OrdinalIgnoreCase))
            RetargetReferences(oldResolved, newResolved);

        var description = ApplyTarget(newResolved);

        _assigned.Add(new Assignment(newResolved, oldResolved, description));

        // Keep the candidate map in step so a re-press of the same control resolves correctly.
        _candidates.Remove(oldResolved);
        _candidates[newResolved] = candidate with { ResolvedTag = newResolved };

        Arm();
    }

    /// <summary>
    /// Points a learned tag at whatever the operator chose. Reuses an existing mapping for the
    /// same control rather than stacking duplicates, since relearning a button is routine.
    /// </summary>
    private string ApplyTarget(string tag)
    {
        var device = _config.VJoy.Devices.FirstOrDefault();
        if (device is null && Target != LearnTarget.NameOnly)
            return "no vJoy device configured";

        switch (Target)
        {
            case LearnTarget.Button:
            {
                var number = int.TryParse(ButtonBox.Text, out var n) ? Math.Max(1, n) : 1;
                var mode = (VJoyButtonMode)(ButtonModeCombo.SelectedItem ?? VJoyButtonMode.Momentary);

                foreach (var stale in device!.Buttons
                             .Where(b => string.Equals(b.Tag, tag, StringComparison.OrdinalIgnoreCase))
                             .ToList())
                    device.Buttons.Remove(stale);

                var existing = device.Buttons.FirstOrDefault(b => b.Button == number);
                if (existing is not null) device.Buttons.Remove(existing);

                device.Buttons.Add(new VJoyButtonMapping { Tag = tag, Button = number, Mode = mode });
                return $"button {number} ({mode})";
            }

            case LearnTarget.HatDirection:
            {
                var pov = int.TryParse(PovBox.Text, out var p) ? Math.Clamp(p, 1, 4) : 1;
                var kind = (VJoyPovKind)(HatKindCombo.SelectedItem ?? VJoyPovKind.Continuous);
                var direction = HatDirectionCombo.SelectedIndex;

                // One mapping per hat, gathering four contacts - so learning "Up" then "Left"
                // fills in the same hat rather than creating two.
                var mapping = device!.Povs.FirstOrDefault(x => x.Pov == pov);
                if (mapping is null)
                {
                    mapping = new VJoyPovMapping { Pov = pov, Source = VJoyPovSource.Contacts };
                    device.Povs.Add(mapping);
                }
                mapping.Source = VJoyPovSource.Contacts;
                mapping.Kind = kind;

                ClearContact(mapping, tag);
                switch (direction)
                {
                    case 0: mapping.UpTag = tag; break;
                    case 1: mapping.RightTag = tag; break;
                    case 2: mapping.DownTag = tag; break;
                    default: mapping.LeftTag = tag; break;
                }

                var names = new[] { "Up", "Right", "Down", "Left" };
                return $"hat {pov} {names[direction]} ({kind})";
            }

            case LearnTarget.Axis:
            {
                var axis = (VJoyAxis)(AxisCombo.SelectedItem ?? VJoyAxis.X);
                var min = double.TryParse(AxisMinBox.Text, out var lo) ? lo : 0;
                var max = double.TryParse(AxisMaxBox.Text, out var hi) ? hi : 1;

                foreach (var stale in device!.Axes
                             .Where(a => string.Equals(a.Tag, tag, StringComparison.OrdinalIgnoreCase)
                                         || a.Axis == axis)
                             .ToList())
                    device.Axes.Remove(stale);

                device.Axes.Add(new VJoyAxisMapping { Tag = tag, Axis = axis, InputMin = min, InputMax = max });
                return $"axis {axis} ({min:0.##}..{max:0.##})";
            }

            default:
                return "name only";
        }
    }

    /// <summary>Drops a tag from every direction slot, so re-learning it cannot leave a stale copy.</summary>
    private static void ClearContact(VJoyPovMapping mapping, string tag)
    {
        bool Same(string other) => string.Equals(other, tag, StringComparison.OrdinalIgnoreCase);
        if (Same(mapping.UpTag)) mapping.UpTag = "";
        if (Same(mapping.RightTag)) mapping.RightTag = "";
        if (Same(mapping.DownTag)) mapping.DownTag = "";
        if (Same(mapping.LeftTag)) mapping.LeftTag = "";
    }

    /// <summary>
    /// Renaming a point has to carry every downstream reference with it, or the rename silently
    /// unhooks a vJoy button or an HMI register.
    /// </summary>
    private void RetargetReferences(string oldTag, string newTag)
    {
        bool Same(string value) => string.Equals(value, oldTag, StringComparison.OrdinalIgnoreCase);

        foreach (var device in _config.VJoy.Devices)
        {
            foreach (var button in device.Buttons.Where(b => Same(b.Tag))) button.Tag = newTag;
            foreach (var axis in device.Axes.Where(a => Same(a.Tag))) axis.Tag = newTag;
            foreach (var pov in device.Povs)
            {
                if (Same(pov.Tag)) pov.Tag = newTag;
                if (Same(pov.UpTag)) pov.UpTag = newTag;
                if (Same(pov.RightTag)) pov.RightTag = newTag;
                if (Same(pov.DownTag)) pov.DownTag = newTag;
                if (Same(pov.LeftTag)) pov.LeftTag = newTag;
            }
        }

        // Server blocks store tags relative to their own prefix, so compare on the resolved name.
        foreach (var server in _config.Servers)
        {
            foreach (var map in server.Maps)
            {
                foreach (var block in map.Blocks)
                {
                    foreach (var point in block.Points)
                    {
                        if (!Same(map.TagPrefix + point.Tag)) continue;
                        point.Tag = newTag.StartsWith(map.TagPrefix, StringComparison.OrdinalIgnoreCase)
                            ? newTag[map.TagPrefix.Length..]
                            : newTag;
                    }
                }
            }
        }

        foreach (var feedback in _config.Telemetry.Feedback.Where(f => Same(f.Tag)))
            feedback.Tag = newTag;

        foreach (var client in _config.Clients)
        {
            foreach (var group in client.WriteGroups)
            {
                foreach (var point in group.Points)
                {
                    if (Same(client.TagPrefix + point.Tag))
                        point.Tag = newTag.StartsWith(client.TagPrefix, StringComparison.OrdinalIgnoreCase)
                            ? newTag[client.TagPrefix.Length..]
                            : newTag;
                }
            }
        }
    }

    private void UpdateCount() =>
        CountText.Text = _assigned.Count == 0 ? "" : $"{_assigned.Count} assigned";

    private void OnRearm(object sender, RoutedEventArgs e) => Arm();

    private void OnDone(object sender, RoutedEventArgs e) => DialogResult = _assigned.Count > 0;
}
