using System.Collections.ObjectModel;
using ModbusBridge.Core.Config;
using ModbusBridge.Core.Data;

namespace ModbusBridge.AvaloniaApp;

/// <summary>
/// Backs the DataGrid spike with the real thing: the point list out of the live config, or a
/// generated one of the same size when no config is present.
///
/// Size matters here. The HMI block has 317 points and the full component map is 1409; a grid that
/// is pleasant with twenty rows and unusable with a thousand would not have shown up in a toy.
/// </summary>
public sealed class GridSpikeViewModel
{
    public ObservableCollection<PointConfig> Points { get; } = new();

    public PointDataType[] DataTypes { get; } = Enum.GetValues<PointDataType>();
    public AccessMode[] AccessModes { get; } = Enum.GetValues<AccessMode>();

    public string Summary { get; }

    public GridSpikeViewModel()
    {
        var loaded = TryLoadLiveConfig();
        if (loaded.Count > 0)
        {
            foreach (var point in loaded) Points.Add(point);
            Summary = $"{Points.Count} point(s) from the live configuration. " +
                      "Text and check-box cells edit in place; Data type and Access are template " +
                      "columns, because Avalonia has no DataGridComboBoxColumn.";
        }
        else
        {
            for (var i = 0; i < 317; i++)
                Points.Add(new PointConfig
                {
                    Tag = $"sim.point{i:D3}",
                    Offset = i,
                    DataType = (PointDataType)(i % 4),
                    Access = (AccessMode)(i % 3),
                    Description = $"generated row {i}"
                });
            Summary = $"{Points.Count} generated point(s) - no configuration found. " +
                      "Text and check-box cells edit in place; Data type and Access are template columns.";
        }
    }

    private static List<PointConfig> TryLoadLiveConfig()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "rig", "config", "bridge.json"),
                     Path.Combine(Environment.CurrentDirectory, "rig", "config", "bridge.json"),
                 })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var config = new ConfigService(candidate).Load();
                return config.Servers
                             .SelectMany(s => s.Maps)
                             .SelectMany(m => m.Blocks)
                             .SelectMany(b => b.Points)
                             .ToList();
            }
            catch
            {
                // A spike must not fall over on a config it cannot read - it generates instead.
            }
        }
        return new List<PointConfig>();
    }
}
