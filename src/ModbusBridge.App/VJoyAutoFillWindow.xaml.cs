using System.Windows;
using ModbusBridge.Core.Config;

namespace ModbusBridge.App;

/// <summary>
/// Bulk button mapper. Wiring a 60-button panel one row at a time is the tedious part of setting
/// this up, so this generates the whole run in one step.
/// </summary>
public partial class VJoyAutoFillWindow : Window
{
    public VJoyAutoFillWindow(int firstButton)
    {
        InitializeComponent();

        ModeCombo.ItemsSource = Enum.GetValues<VJoyButtonMode>();
        ModeCombo.SelectedItem = VJoyButtonMode.Momentary;

        FirstButtonBox.Text = firstButton.ToString();
        UpdatePreview();
    }

    private int Count => int.TryParse(CountBox.Text, out var value) ? Math.Max(0, value) : 0;
    private int FirstIndex => int.TryParse(FirstIndexBox.Text, out var value) ? value : 1;
    private int Digits => int.TryParse(DigitsBox.Text, out var value) ? Math.Clamp(value, 1, 6) : 2;
    private int FirstButton => int.TryParse(FirstButtonBox.Text, out var value) ? Math.Max(1, value) : 1;
    private int PulseMs => int.TryParse(PulseBox.Text, out var value) ? Math.Max(1, value) : 50;
    private VJoyButtonMode Mode => (VJoyButtonMode)(ModeCombo.SelectedItem ?? VJoyButtonMode.Momentary);

    public IReadOnlyList<VJoyButtonMapping> Generate()
    {
        var mappings = new List<VJoyButtonMapping>();

        for (var i = 0; i < Count; i++)
        {
            var button = FirstButton + i;
            if (button > 128) break;      // vJoy's hard ceiling

            var index = (FirstIndex + i).ToString(new string('0', Digits));

            mappings.Add(new VJoyButtonMapping
            {
                Tag = PatternBox.Text.Replace("{n}", index),
                Button = button,
                Mode = Mode,
                PulseMs = PulseMs,
                Invert = InvertBox.IsChecked == true,
                Description = string.IsNullOrWhiteSpace(DescriptionBox.Text)
                    ? null
                    : DescriptionBox.Text.Replace("{n}", index)
            });
        }

        return mappings;
    }

    private void UpdatePreview()
    {
        var mappings = Generate();
        if (mappings.Count == 0)
        {
            PreviewText.Text = "Nothing to generate.";
            return;
        }

        var lines = mappings.Take(3).Select(m => $"{m.Tag,-24} -> button {m.Button}").ToList();
        if (mappings.Count > 3) lines.Add("...");
        lines.Add($"{mappings[^1].Tag,-24} -> button {mappings[^1].Button}");

        var truncated = Count > mappings.Count
            ? $"\n\nNOTE: clipped to {mappings.Count} - vJoy supports at most 128 buttons."
            : "";

        PreviewText.Text = string.Join("\n", lines) + truncated;
    }

    private void OnPreview(object sender, RoutedEventArgs e) => UpdatePreview();

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnGenerate(object sender, RoutedEventArgs e)
    {
        if (Count == 0)
        {
            MessageBox.Show("Set a count of at least 1.", "Auto-fill",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
