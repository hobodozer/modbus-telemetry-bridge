using System.Windows;
using ModbusBridge.Core.Config;
using ModbusBridge.Core.Data;

namespace ModbusBridge.App;

/// <summary>
/// Bulk point generator. Wiring 60 buttons or a bank of gauges one row at a time is the main thing
/// that makes a register editor painful, so this produces the whole run in one go.
/// </summary>
public partial class AutoFillWindow : Window
{
    private readonly ModbusArea _area;
    private readonly int _span;

    public AutoFillWindow(ModbusArea area, int span, AccessMode access)
    {
        InitializeComponent();
        _area = area;
        _span = span;

        var isBitArea = area is ModbusArea.Coil or ModbusArea.DiscreteInput;

        TypeCombo.ItemsSource = Enum.GetValues<PointDataType>();
        TypeCombo.SelectedItem = isBitArea ? PointDataType.Bool : PointDataType.Float32;
        TypeCombo.IsEnabled = !isBitArea;

        AccessCombo.ItemsSource = Enum.GetValues<AccessMode>();
        AccessCombo.SelectedItem = access;

        PackBitsBox.IsEnabled = !isBitArea;

        Title = $"Auto-fill points - {area}, span {span}";
        UpdatePreview();
    }

    private int Count => int.TryParse(CountBox.Text, out var value) ? Math.Max(0, value) : 0;
    private int FirstIndex => int.TryParse(FirstIndexBox.Text, out var value) ? value : 1;
    private int Digits => int.TryParse(DigitsBox.Text, out var value) ? Math.Clamp(value, 1, 6) : 2;
    private int FirstOffset => int.TryParse(FirstOffsetBox.Text, out var value) ? Math.Max(0, value) : 0;
    private PointDataType SelectedType => (PointDataType)(TypeCombo.SelectedItem ?? PointDataType.UInt16);
    private AccessMode SelectedAccess => (AccessMode)(AccessCombo.SelectedItem ?? AccessMode.Read);

    private bool PackBits =>
        PackBitsBox.IsChecked == true && PackBitsBox.IsEnabled && SelectedType == PointDataType.Bool;

    /// <summary>Builds the points described by the form. Called after the dialog returns true.</summary>
    public IReadOnlyList<PointConfig> Generate()
    {
        var points = new List<PointConfig>();
        var isBitArea = _area is ModbusArea.Coil or ModbusArea.DiscreteInput;
        var type = isBitArea ? PointDataType.Bool : SelectedType;

        var gain = ParseDouble(GainBox.Text, 1);
        var offset = ParseDouble(OffsetBox.Text, 0);
        var deadband = ParseDouble(DeadbandBox.Text, 0);
        var needsScaling = gain != 1 || offset != 0 || deadband != 0;

        var size = ValueCodec.RegisterCount(type);

        for (var i = 0; i < Count; i++)
        {
            var index = (FirstIndex + i).ToString(new string('0', Digits));

            int pointOffset;
            var bitIndex = -1;

            if (isBitArea)
            {
                pointOffset = FirstOffset + i;
            }
            else if (PackBits)
            {
                pointOffset = FirstOffset + i / 16;
                bitIndex = i % 16;
            }
            else
            {
                pointOffset = FirstOffset + i * size;
            }

            points.Add(new PointConfig
            {
                Tag = PatternBox.Text.Replace("{n}", index),
                Offset = pointOffset,
                DataType = type,
                BitIndex = bitIndex,
                Access = SelectedAccess,
                Invert = InvertBox.IsChecked == true,
                Description = string.IsNullOrWhiteSpace(DescriptionBox.Text)
                    ? null
                    : DescriptionBox.Text.Replace("{n}", index),
                Scale = needsScaling
                    ? new ScalingConfig { Gain = gain, Offset = offset, Deadband = deadband }
                    : new ScalingConfig()
            });
        }

        return points;
    }

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text, out var value) ? value : fallback;

    private void UpdatePreview()
    {
        var points = Generate();
        if (points.Count == 0)
        {
            PreviewText.Text = "Nothing to generate.";
            return;
        }

        var lines = points.Take(3)
            .Select(p => $"{p.Tag,-22} offset {p.Offset}" + (p.BitIndex >= 0 ? $".{p.BitIndex}" : ""))
            .ToList();
        if (points.Count > 3) lines.Add("...");
        var last = points[^1];
        lines.Add($"{last.Tag,-22} offset {last.Offset}" + (last.BitIndex >= 0 ? $".{last.BitIndex}" : ""));

        var end = points.Max(p => p.Offset + p.Size);
        var overflow = _span > 0 && end > _span
            ? $"\n\nWARNING: this runs to offset {end - 1}, past the block span of {_span}. " +
              "Enlarge the block or the config will not validate."
            : "";

        PreviewText.Text = string.Join("\n", lines) + overflow;
    }

    private void OnPreview(object sender, RoutedEventArgs e) => UpdatePreview();

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnGenerate(object sender, RoutedEventArgs e)
    {
        if (Count == 0)
        {
            MessageBox.Show("Set a count of at least 1.", "Auto-fill", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
