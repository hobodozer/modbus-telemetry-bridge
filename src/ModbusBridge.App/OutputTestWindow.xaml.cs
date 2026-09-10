using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ModbusBridge.App.ViewModels;
using ModbusBridge.Core.Config;
using ModbusBridge.Core.Data;
using ModbusBridge.Core.Diagnostics;
using ModbusBridge.Core.Tags;

namespace ModbusBridge.App;

/// <summary>
/// A commissioning panel for outputs. Finding out which relay is which by editing config and
/// restarting is miserable; this drives each one directly so you can watch or listen.
///
/// It works by forcing the tag a write group already sends, rather than opening its own
/// connection - the PLC's MB_SERVER serves exactly one TCP connection and the engine owns it.
/// </summary>
public partial class OutputTestWindow : Window
{
    public sealed class OutputRow : ObservableObject
    {
        public required string Tag { get; init; }
        public required string Where { get; init; }
        public required TagEntry Entry { get; init; }

        public bool IsOn => Entry.Value.Number >= 0.5;
        public bool IsForced => Entry.IsForced;

        public string StateText => !IsForced ? "auto" : IsOn ? "ON" : "off";

        public Brush StateBrush => !IsForced
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x5A, 0x62))
            : IsOn
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xC3, 0x8A))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x90, 0x98));

        public void Refresh()
        {
            Raise(nameof(IsOn));
            Raise(nameof(IsForced));
            Raise(nameof(StateText));
            Raise(nameof(StateBrush));
        }
    }

    private readonly ObservableCollection<OutputRow> _rows = new();
    private readonly DispatcherTimer _refresh;
    private readonly List<TagEntry> _pulsing = new();

    private DispatcherTimer? _sweep;
    private int _sweepIndex = -1;

    public OutputTestWindow(TagBus bus, BridgeConfig config)
    {
        InitializeComponent();

        foreach (var client in config.Clients.Where(c => c.Enabled))
        {
            foreach (var group in client.WriteGroups.Where(g => g.Enabled))
            {
                foreach (var point in group.Points.Where(p => p.Enabled))
                {
                    var resolved = client.TagPrefix + point.Tag;
                    _rows.Add(new OutputRow
                    {
                        Tag = resolved,
                        Where = $"{client.Name} / {group.Name} / {group.Area} offset {point.Offset}" +
                                (string.IsNullOrWhiteSpace(point.Description) ? "" : $"  -  {point.Description}"),
                        Entry = bus.GetOrAdd(resolved)
                    });
                }
            }
        }

        OutputList.ItemsSource = _rows;
        StateText.Text = _rows.Count == 0
            ? "No write groups configured - nothing to test."
            : $"{_rows.Count} output(s)";

        _refresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _refresh.Tick += (_, _) => { foreach (var row in _rows) row.Refresh(); };
        _refresh.Start();

        // Leaving an output forced after the panel closes would be a genuine hazard - the value
        // would stick with nothing on screen explaining why.
        Closed += (_, _) =>
        {
            _refresh.Stop();
            StopSweep();
            ReleaseAll();
        };
    }

    private int DwellMs => int.TryParse(DwellBox.Text, out var value) ? Math.Clamp(value, 50, 10000) : 600;

    private static void Set(OutputRow row, bool on) => row.Entry.Force(TagValue.Good(on));

    private void OnRowOn(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is OutputRow row) { Set(row, true); row.Refresh(); }
    }

    private void OnRowOff(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is OutputRow row) { Set(row, false); row.Refresh(); }
    }

    private async void OnRowPulse(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not OutputRow row) return;
        if (_pulsing.Contains(row.Entry)) return;

        _pulsing.Add(row.Entry);
        try
        {
            Set(row, true);
            row.Refresh();
            await Task.Delay(DwellMs);
            Set(row, false);
            row.Refresh();
        }
        finally
        {
            _pulsing.Remove(row.Entry);
        }
    }

    private void OnAllOff(object sender, RoutedEventArgs e)
    {
        StopSweep();
        foreach (var row in _rows) { Set(row, false); row.Refresh(); }
    }

    private void OnAllOn(object sender, RoutedEventArgs e)
    {
        StopSweep();
        foreach (var row in _rows) { Set(row, true); row.Refresh(); }
    }

    private void OnReleaseAll(object sender, RoutedEventArgs e)
    {
        StopSweep();
        ReleaseAll();
    }

    private void ReleaseAll()
    {
        foreach (var row in _rows)
        {
            row.Entry.Unforce();
            row.Refresh();
        }
        Log.Info("ui", "Output test: released every forced output.");
    }

    /// <summary>Walks the outputs one at a time, which is how you find out which relay is which.</summary>
    private void OnSweep(object sender, RoutedEventArgs e)
    {
        if (_sweep is not null) { StopSweep(); return; }
        if (_rows.Count == 0) return;

        foreach (var row in _rows) Set(row, false);

        _sweepIndex = -1;
        SweepButton.Content = "Stop";
        _sweep = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DwellMs) };
        _sweep.Tick += (_, _) =>
        {
            if (_sweepIndex >= 0 && _sweepIndex < _rows.Count) Set(_rows[_sweepIndex], false);

            _sweepIndex++;
            if (_sweepIndex >= _rows.Count) { StopSweep(); return; }

            Set(_rows[_sweepIndex], true);
            StateText.Text = $"sweeping: {_rows[_sweepIndex].Tag}";
            foreach (var row in _rows) row.Refresh();
        };
        _sweep.Start();
    }

    private void StopSweep()
    {
        if (_sweep is null) return;
        _sweep.Stop();
        _sweep = null;

        if (_sweepIndex >= 0 && _sweepIndex < _rows.Count) Set(_rows[_sweepIndex], false);
        _sweepIndex = -1;

        SweepButton.Content = "Sweep";
        StateText.Text = $"{_rows.Count} output(s)";
        foreach (var row in _rows) row.Refresh();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
