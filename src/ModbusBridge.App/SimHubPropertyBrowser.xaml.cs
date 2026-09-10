using System.Windows;
using System.Windows.Controls;
using ModbusBridge.Core.Config;
using ModbusBridge.Core.Inputs;
using ModbusBridge.Telemetry;

namespace ModbusBridge.App;

/// <summary>
/// Browses everything SimHub currently exposes and turns a selection into subscriptions.
/// The list comes from the running plugin, so it reflects the game that is actually loaded rather
/// than a hard-coded list that goes stale.
/// </summary>
public partial class SimHubPropertyBrowser : Window
{
    private sealed record Row(string Name, string Type, string SuggestedTag, string AlreadyAdded);

    private readonly TelemetryIngestServer? _ingest;
    private readonly TelemetryConfig _config;
    private readonly HashSet<string> _existing;
    private List<Row> _allRows = new();

    public SimHubPropertyBrowser(TelemetryIngestServer? ingest, TelemetryConfig config)
    {
        InitializeComponent();

        _ingest = ingest;
        _config = config;
        _existing = config.Subscriptions
            .Select(s => s.Property)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        PrefixBox.Text = config.TagPrefix;
        Rebuild();
    }

    /// <summary>Subscriptions the user chose. Empty when the dialog was cancelled.</summary>
    public List<TelemetrySubscription> Selected { get; } = new();

    private void Rebuild()
    {
        var catalog = _ingest?.Catalog ?? Array.Empty<TelemetryProperty>();

        if (_ingest is null)
        {
            StatusText.Text = "The engine is not running, so no property list is available. " +
                              "Start the engine with telemetry enabled and make sure SimHub is running " +
                              "with the bridge plugin installed.";
        }
        else if (catalog.Count == 0)
        {
            StatusText.Text = "No property list yet. SimHub must be running with the bridge plugin " +
                              "installed and pointed at this bridge's telemetry port. " +
                              "Press \"Refresh from SimHub\" once it is connected.";
        }
        else
        {
            StatusText.Text = $"{catalog.Count} propertie(s) reported by SimHub. " +
                              "Select one or more rows - Ctrl or Shift click for several.";
        }

        _allRows = catalog
            .Select(p => new Row(
                p.Name,
                p.Type.ToString(),
                _config.TagPrefix + TelemetrySubscription.SanitiseName(p.Name),
                _existing.Contains(p.Name) ? "added" : ""))
            .ToList();

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var search = SearchBox.Text?.Trim() ?? "";

        var rows = search.Length == 0
            ? _allRows
            : _allRows.Where(r => r.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

        PropertyGrid.ItemsSource = rows;
        CountText.Text = search.Length == 0
            ? $"{rows.Count} propertie(s)"
            : $"{rows.Count} of {_allRows.Count} match";
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SelectionText.Text = PropertyGrid.SelectedItems.Count == 0
            ? ""
            : $"{PropertyGrid.SelectedItems.Count} selected";

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_ingest is null)
        {
            MessageBox.Show("The engine is not running.", "Refresh",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_ingest.RequestCatalog())
        {
            MessageBox.Show("No SimHub plugin has connected yet, so there is nothing to ask.\n\n" +
                            "Check that SimHub is running, the bridge plugin is installed, and its " +
                            "configured port matches this bridge's telemetry listen port.",
                            "Refresh", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StatusText.Text = "Asked SimHub for a fresh property list; it should arrive within a second. " +
                          "Press Refresh again to display it.";
        Rebuild();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var rows = PropertyGrid.SelectedItems.Cast<Row>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show("Select one or more properties first.", "Add properties",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var row in rows)
        {
            // Silently skip anything already subscribed, so a sloppy multi-select is harmless.
            if (_existing.Contains(row.Name)) continue;
            Selected.Add(new TelemetrySubscription { Property = row.Name });
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
