using System.Windows.Media;
using ModbusBridge.Core.Config;
using ModbusBridge.Core.Tags;

namespace ModbusBridge.App.ViewModels;

/// <summary>
/// One lamp in the operator console: a vJoy button and whatever tag drives it. Mirrors what the
/// game sees rather than what the PLC sent, so a mapping mistake shows up here as a dark lamp.
/// </summary>
public sealed class PanelLampViewModel : ObservableObject
{
    private readonly TagEntry? _tag;

    public PanelLampViewModel(int number, string tag, TagEntry? entry)
    {
        Number = number;
        Tag = tag;
        _tag = entry;
    }

    public int Number { get; }
    public string Tag { get; }

    public bool IsOn => _tag is not null && _tag.Value.Number >= 0.5;
    public bool IsStale => _tag is null || _tag.Value.Quality < ModbusBridge.Core.Data.TagQuality.Good;

    public Brush Fill => IsStale
        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x41, 0x4D))
        : IsOn
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3F, 0xB6, 0x5E))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x23, 0x27, 0x2E));

    public string ToolTipText => $"button {Number}  <-  {Tag}";

    public void Refresh()
    {
        Raise(nameof(IsOn));
        Raise(nameof(IsStale));
        Raise(nameof(Fill));
    }
}

/// <summary>One axis bar, scaled through the same input range the feeder uses.</summary>
public sealed class PanelAxisViewModel : ObservableObject
{
    private readonly TagEntry? _tag;
    private readonly VJoyAxisMapping _config;

    public PanelAxisViewModel(VJoyAxisMapping config, TagEntry? entry)
    {
        _config = config;
        _tag = entry;
    }

    public string Name => _config.Axis.ToString();
    public string Tag => _config.Tag;

    /// <summary>Position as a percentage of travel, clamped - a bar past its end reads as broken.</summary>
    public double Percent
    {
        get
        {
            if (_tag is null) return 0;
            var span = _config.InputMax - _config.InputMin;
            if (Math.Abs(span) < 1e-9) return 0;
            var fraction = (_tag.Value.Number - _config.InputMin) / span;
            return Math.Clamp(fraction, 0, 1) * 100;
        }
    }

    public string Readout => _tag is null ? "-" : $"{Percent:0}%";

    public void Refresh()
    {
        Raise(nameof(Percent));
        Raise(nameof(Readout));
    }
}
