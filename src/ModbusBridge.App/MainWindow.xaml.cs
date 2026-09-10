using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using ModbusBridge.App.ViewModels;
using ModbusBridge.Core.Config;
using ModbusBridge.Core.Data;
using ModbusBridge.Core.Outputs.VJoy;

namespace ModbusBridge.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ClientList.Items.Count > 0) ClientList.SelectedIndex = 0;
        if (ServerList.Items.Count > 0) ServerList.SelectedIndex = 0;

        // Keep the log pinned to the newest entry while "Follow" is ticked.
        ((INotifyCollectionChanged)Vm.LogRows).CollectionChanged += (_, args) =>
        {
            if (AutoScroll.IsChecked != true) return;
            if (args.Action != NotifyCollectionChangedAction.Add) return;
            if (LogGrid.Items.Count > 0) LogGrid.ScrollIntoView(LogGrid.Items[^1]);
        };
    }

    /// <summary>Self-test support: how many top-level tabs there are.</summary>
    /// <summary>
    /// Navigation drives the tab control, which still holds every panel. Keeping the tabs means
    /// the existing content, bindings and handlers are untouched by the new shell.
    /// </summary>
    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not ListBoxItem item) return;
        if (item.Tag is not string tag || !int.TryParse(tag, out var index)) return;
        if (index >= 0 && index < RootTabs.Items.Count) RootTabs.SelectedIndex = index;
    }

    /// <summary>Keeps the navigation highlight in step when something selects a tab in code.</summary>
    private void SyncNavToTab()
    {
        foreach (var candidate in NavList.Items)
        {
            if (candidate is not ListBoxItem item) continue;
            if (item.Tag is string tag && int.TryParse(tag, out var index) && index == RootTabs.SelectedIndex)
            {
                NavList.SelectedItem = item;
                return;
            }
        }
    }

    internal int SelectAllTabsCount() => RootTabs.Items.Count;

    /// <summary>
    /// Self-test support: selects a tab (and every nested tab inside it) so WPF realises the
    /// templates, then returns the tab's header for the log.
    /// </summary>
    internal string SelectTab(int index)
    {
        RootTabs.SelectedIndex = index;
        SyncNavToTab();
        RootTabs.UpdateLayout();

        // Walk the nested TabControls too - read/write groups and the watchdog live inside one.
        foreach (var nested in FindNestedTabControls(RootTabs.SelectedContent as DependencyObject))
        {
            for (var i = 0; i < nested.Items.Count; i++)
            {
                nested.SelectedIndex = i;
                nested.UpdateLayout();
            }
            nested.SelectedIndex = 0;
        }

        return (RootTabs.SelectedItem as TabItem)?.Header?.ToString() ?? $"tab {index}";
    }

    private static IEnumerable<TabControl> FindNestedTabControls(DependencyObject? root)
    {
        if (root is null) yield break;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TabControl tabControl) yield return tabControl;
            foreach (var nested in FindNestedTabControls(child)) yield return nested;
        }
    }

    /// <summary>Set by the shutdown paths so the close actually goes through instead of hiding.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing the window sends the bridge to the tray; Exit from the tray menu really quits.
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    // ---- Tag actions ----------------------------------------------------------------------

    private TagRowViewModel? SelectedTag => TagGrid.SelectedItem as TagRowViewModel;

    private bool TryReadValueBox(out double value)
    {
        if (double.TryParse(ValueBox.Text, out value)) return true;

        // Accept the obvious boolean spellings too, since most points here are bits.
        var text = ValueBox.Text.Trim();
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            value = 1;
            return true;
        }
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            value = 0;
            return true;
        }

        MessageBox.Show("Enter a number, or true / false.", "Value", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void OnWriteTag(object sender, RoutedEventArgs e)
    {
        if (SelectedTag is not { } row) return;
        if (TryReadValueBox(out var value)) Vm.WriteTag(row, value);
    }

    private void OnForceTag(object sender, RoutedEventArgs e)
    {
        if (SelectedTag is not { } row) return;
        if (TryReadValueBox(out var value)) Vm.ForceTag(row, value);
    }

    private void OnUnforceTag(object sender, RoutedEventArgs e)
    {
        if (SelectedTag is { } row) Vm.UnforceTag(row);
    }

    // ---- Device editing -------------------------------------------------------------------

    private ModbusClientConfig? SelectedClient => ClientList.SelectedItem as ModbusClientConfig;

    private void OnAddClient(object sender, RoutedEventArgs e)
    {
        var client = new ModbusClientConfig
        {
            Name = $"PLC {Vm.Clients.Count + 1}",
            TagPrefix = $"plc{Vm.Clients.Count + 1}."
        };
        Vm.Clients.Add(client);
        ClientList.SelectedItem = client;
    }

    private void OnDuplicateClient(object sender, RoutedEventArgs e)
    {
        if (SelectedClient is not { } source) return;

        // Round-trip through JSON: a deep copy without hand-written clone code for every type.
        var copy = ConfigService.Deserialize(
            $"{{\"clients\":[{System.Text.Json.JsonSerializer.Serialize(source, ConfigService.SerializerOptions)}]}}")
            .Clients[0];
        copy.Id = Guid.NewGuid().ToString("N")[..8];
        copy.Name = source.Name + " (copy)";
        Vm.Clients.Add(copy);
        ClientList.SelectedItem = copy;
    }

    private void OnDeleteClient(object sender, RoutedEventArgs e)
    {
        if (SelectedClient is not { } client) return;
        if (MessageBox.Show($"Delete device '{client.Name}' and all its groups?", "Delete device",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Vm.Clients.Remove(client);
    }

    private void OnClientAddressPreset(object sender, RoutedEventArgs e)
    {
        if (SelectedClient is not { } client) return;

        var choice = MessageBox.Show(
            "Yes - Modicon notation (coils 1, discrete 10001, input regs 30001, holding regs 40001).\n" +
            "No - one-based (every area numbers from 1; this is what Siemens MB_SERVER docs use).\n" +
            "Cancel - zero-based wire addresses.",
            "Address base preset", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        switch (choice)
        {
            case MessageBoxResult.Yes:
                client.AddressBaseByArea = new Dictionary<ModbusArea, int>(Addressing.Modicon);
                client.AddressBase = 0;
                break;
            case MessageBoxResult.No:
                client.AddressBaseByArea = null;
                client.AddressBase = 1;
                break;
            case MessageBoxResult.Cancel:
                client.AddressBaseByArea = null;
                client.AddressBase = 0;
                break;
            default:
                return;
        }

        Vm.MarkDirty();
        // Rebind so the address-base box shows the new value.
        var selected = ClientList.SelectedItem;
        ClientList.SelectedItem = null;
        ClientList.SelectedItem = selected;
    }

    private void OnAddReadGroup(object sender, RoutedEventArgs e)
    {
        if (SelectedClient is not { } client) return;
        var group = new ReadGroupConfig
        {
            Name = $"read group {client.ReadGroups.Count + 1}",
            Area = ModbusArea.HoldingRegister,
            StartAddress = client.AddressBase,
            Count = 16,
            PollIntervalMs = 20
        };
        client.ReadGroups.Add(group);
        ReadGroupGrid.SelectedItem = group;
        Vm.MarkDirty();
    }

    private void OnDeleteReadGroup(object sender, RoutedEventArgs e)
    {
        if (SelectedClient is not { } client) return;
        if (ReadGroupGrid.SelectedItem is not ReadGroupConfig group) return;
        client.ReadGroups.Remove(group);
        Vm.MarkDirty();
    }

    private void OnAddReadPoint(object sender, RoutedEventArgs e)
    {
        if (ReadGroupGrid.SelectedItem is not ReadGroupConfig group) return;
        group.Points.Add(NewPoint(group.Area, group.Points.Count, AccessMode.Read));
        Vm.MarkDirty();
    }

    private void OnDeleteReadPoint(object sender, RoutedEventArgs e) =>
        DeleteSelectedPoints(ReadPointGrid);

    private void OnAutoFillReadPoints(object sender, RoutedEventArgs e)
    {
        if (ReadGroupGrid.SelectedItem is not ReadGroupConfig group)
        {
            MessageBox.Show("Select a read group first.", "Auto-fill", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RunAutoFill(group.Points, group.Area, group.Count, AccessMode.Read);
    }

    private void OnAddWriteGroup(object sender, RoutedEventArgs e)
    {
        if (SelectedClient is not { } client) return;
        var group = new WriteGroupConfig
        {
            Name = $"write group {client.WriteGroups.Count + 1}",
            Area = ModbusArea.HoldingRegister,
            StartAddress = client.AddressBase,
            Mode = WriteMode.OnChangeAndPeriodic,
            PeriodMs = 20
        };
        client.WriteGroups.Add(group);
        WriteGroupGrid.SelectedItem = group;
        Vm.MarkDirty();
    }

    private void OnDeleteWriteGroup(object sender, RoutedEventArgs e)
    {
        if (SelectedClient is not { } client) return;
        if (WriteGroupGrid.SelectedItem is not WriteGroupConfig group) return;
        client.WriteGroups.Remove(group);
        Vm.MarkDirty();
    }

    private void OnAddWritePoint(object sender, RoutedEventArgs e)
    {
        if (WriteGroupGrid.SelectedItem is not WriteGroupConfig group) return;
        group.Points.Add(NewPoint(group.Area, group.Points.Count, AccessMode.Write));
        Vm.MarkDirty();
    }

    private void OnDeleteWritePoint(object sender, RoutedEventArgs e) =>
        DeleteSelectedPoints(WritePointGrid);

    // ---- Server editing -------------------------------------------------------------------

    private ModbusServerConfig? SelectedServer => ServerList.SelectedItem as ModbusServerConfig;

    private void OnAddServer(object sender, RoutedEventArgs e)
    {
        var server = new ModbusServerConfig
        {
            Name = $"server {Vm.Servers.Count + 1}",
            Port = Vm.Servers.Count == 0 ? 502 : 5020 + Vm.Servers.Count
        };
        server.Maps.Add(new ServerMapConfig { Name = "map 1", UnitId = 1 });
        Vm.Servers.Add(server);
        ServerList.SelectedItem = server;
    }

    private void OnDeleteServer(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is not { } server) return;
        if (MessageBox.Show($"Delete server '{server.Name}' and all its maps?", "Delete server",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Vm.Servers.Remove(server);
    }

    private void OnAddMap(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is not { } server) return;
        var nextUnitId = server.Maps.Count == 0 ? (byte)1 : (byte)(server.Maps.Max(m => m.UnitId) + 1);
        var map = new ServerMapConfig
        {
            Name = $"map {server.Maps.Count + 1}",
            UnitId = nextUnitId,
            AcceptAnyUnitId = server.Maps.Count == 0
        };
        server.Maps.Add(map);
        MapGrid.SelectedItem = map;
        Vm.MarkDirty();
    }

    private void OnDeleteMap(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is not { } server) return;
        if (MapGrid.SelectedItem is not ServerMapConfig map) return;
        server.Maps.Remove(map);
        Vm.MarkDirty();
    }

    private void OnAddBlock(object sender, RoutedEventArgs e)
    {
        if (MapGrid.SelectedItem is not ServerMapConfig map) return;

        // Start the new block after whatever already exists in that area, so it cannot overlap.
        var area = ModbusArea.HoldingRegister;
        var existing = map.Blocks.Where(b => b.Area == area).ToList();
        var start = existing.Count == 0
            ? (SelectedServer?.BaseFor(area) ?? 0)
            : existing.Max(b => b.StartAddress + b.Size);

        var block = new ServerBlockConfig
        {
            Name = $"block {map.Blocks.Count + 1}",
            Area = area,
            StartAddress = start,
            Size = 32
        };
        map.Blocks.Add(block);
        BlockGrid.SelectedItem = block;
        Vm.MarkDirty();
    }

    private void OnDeleteBlock(object sender, RoutedEventArgs e)
    {
        if (MapGrid.SelectedItem is not ServerMapConfig map) return;
        if (BlockGrid.SelectedItem is not ServerBlockConfig block) return;
        map.Blocks.Remove(block);
        Vm.MarkDirty();
    }

    private void OnAddServerPoint(object sender, RoutedEventArgs e)
    {
        if (BlockGrid.SelectedItem is not ServerBlockConfig block) return;
        block.Points.Add(NewPoint(block.Area, NextFreeOffset(block), AccessMode.Read));
        Vm.MarkDirty();
    }

    private void OnDeleteServerPoint(object sender, RoutedEventArgs e) =>
        DeleteSelectedPoints(ServerPointGrid);

    private void OnAutoFillServerPoints(object sender, RoutedEventArgs e)
    {
        if (BlockGrid.SelectedItem is not ServerBlockConfig block)
        {
            MessageBox.Show("Select a block first.", "Auto-fill", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RunAutoFill(block.Points, block.Area, block.Size, AccessMode.Read);
    }

    // ---- vJoy -----------------------------------------------------------------------------

    private VJoyDeviceConfig? SelectedVJoyDevice => VJoyDeviceList.SelectedItem as VJoyDeviceConfig;

    private void OnAddVJoyDevice(object sender, RoutedEventArgs e)
    {
        var used = Vm.VJoyDevices.Select(d => d.DeviceId).ToHashSet();
        uint next = 1;
        while (next <= 16 && used.Contains(next)) next++;

        var device = new VJoyDeviceConfig { DeviceId = next };
        Vm.VJoyDevices.Add(device);
        VJoyDeviceList.SelectedItem = device;
    }

    private void OnDeleteVJoyDevice(object sender, RoutedEventArgs e)
    {
        if (SelectedVJoyDevice is not { } device) return;
        if (MessageBox.Show($"Delete vJoy device {device.DeviceId} and all its mappings?", "Delete device",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Vm.VJoyDevices.Remove(device);
    }

    private void OnAddVJoyButton(object sender, RoutedEventArgs e)
    {
        if (SelectedVJoyDevice is not { } device) return;
        var next = device.Buttons.Count == 0 ? 1 : device.Buttons.Max(b => b.Button) + 1;
        device.Buttons.Add(new VJoyButtonMapping { Tag = "", Button = Math.Min(next, 128) });
        Vm.MarkDirty();
    }

    private void OnDeleteVJoyButton(object sender, RoutedEventArgs e)
    {
        if (SelectedVJoyDevice is not { } device) return;
        foreach (var item in VJoyButtonGrid.SelectedItems.Cast<VJoyButtonMapping>().ToList())
            device.Buttons.Remove(item);
        Vm.MarkDirty();
    }

    private void OnAutoFillVJoyButtons(object sender, RoutedEventArgs e)
    {
        if (SelectedVJoyDevice is not { } device)
        {
            MessageBox.Show("Select a vJoy device first.", "Auto-fill",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var nextButton = device.Buttons.Count == 0 ? 1 : device.Buttons.Max(b => b.Button) + 1;
        var dialog = new VJoyAutoFillWindow(nextButton) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        foreach (var mapping in dialog.Generate()) device.Buttons.Add(mapping);
        Vm.MarkDirty();
    }

    private void OnTestOutputs(object sender, RoutedEventArgs e)
    {
        if (!Vm.Engine.IsRunning)
        {
            MessageBox.Show("Start the engine first - the test panel drives outputs through the " +
                            "running device connection.", "Test outputs",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new OutputTestWindow(Vm.Engine.Tags, Vm.Config) { Owner = this }.ShowDialog();
    }

    private void OnLearnInputs(object sender, RoutedEventArgs e)
    {
        if (!Vm.Engine.IsRunning)
        {
            MessageBox.Show("Start the engine first - learning watches live tag values.", "Learn",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new LearnInputWindow(Vm.Engine.Tags, Vm.Config) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        Vm.MarkDirty();
    }

    private void OnAddVJoyAxis(object sender, RoutedEventArgs e)
    {
        if (SelectedVJoyDevice is not { } device) return;

        // Offer the next axis that is not already mapped, so a new row is usually ready to use.
        var used = device.Axes.Select(a => a.Axis).ToHashSet();
        var free = Enum.GetValues<VJoyAxis>().FirstOrDefault(a => !used.Contains(a), VJoyAxis.X);

        device.Axes.Add(new VJoyAxisMapping { Tag = "", Axis = free, InputMin = 0, InputMax = 1 });
        Vm.MarkDirty();
    }

    private void OnDeleteVJoyAxis(object sender, RoutedEventArgs e)
    {
        if (SelectedVJoyDevice is not { } device) return;
        foreach (var item in VJoyAxisGrid.SelectedItems.Cast<VJoyAxisMapping>().ToList())
            device.Axes.Remove(item);
        Vm.MarkDirty();
    }

    private void OnAddVJoyPov(object sender, RoutedEventArgs e)
    {
        if (SelectedVJoyDevice is not { } device) return;
        var next = device.Povs.Count == 0 ? 1 : device.Povs.Max(p => p.Pov) + 1;
        device.Povs.Add(new VJoyPovMapping { Tag = "", Pov = Math.Clamp(next, 1, 4) });
        Vm.MarkDirty();
    }

    private void OnDeleteVJoyPov(object sender, RoutedEventArgs e)
    {
        if (SelectedVJoyDevice is not { } device) return;
        foreach (var item in VJoyPovGrid.SelectedItems.Cast<VJoyPovMapping>().ToList())
            device.Povs.Remove(item);
        Vm.MarkDirty();
    }

    // ---- SimHub ---------------------------------------------------------------------------

    private void OnBrowseSimHubProperties(object sender, RoutedEventArgs e)
    {
        var browser = new SimHubPropertyBrowser(Vm.Engine.Telemetry, Vm.Config.Telemetry) { Owner = this };
        if (browser.ShowDialog() != true) return;

        foreach (var subscription in browser.Selected)
            Vm.Config.Telemetry.Subscriptions.Add(subscription);

        if (browser.Selected.Count > 0)
        {
            Vm.MarkDirty();
            Vm.Status = $"Added {browser.Selected.Count} SimHub propertie(s). " +
                        "Apply and restart to begin streaming them.";
        }
    }

    private void OnAddSubscription(object sender, RoutedEventArgs e)
    {
        Vm.Config.Telemetry.Subscriptions.Add(new TelemetrySubscription { Scale = new ScalingConfig() });
        Vm.MarkDirty();
    }

    private void OnDeleteSubscription(object sender, RoutedEventArgs e)
    {
        foreach (var item in SubscriptionGrid.SelectedItems.Cast<TelemetrySubscription>().ToList())
            Vm.Config.Telemetry.Subscriptions.Remove(item);
        Vm.MarkDirty();
    }

    private void OnAddFeedback(object sender, RoutedEventArgs e)
    {
        Vm.Config.Telemetry.Feedback.Add(new TelemetryFeedback());
        Vm.MarkDirty();
    }

    private void OnDeleteFeedback(object sender, RoutedEventArgs e)
    {
        foreach (var item in FeedbackGrid.SelectedItems.Cast<TelemetryFeedback>().ToList())
            Vm.Config.Telemetry.Feedback.Remove(item);
        Vm.MarkDirty();
    }

    // ---- Simulation -----------------------------------------------------------------------

    private void OnAddSimTag(object sender, RoutedEventArgs e)
    {
        Vm.Config.Simulation.Tags.Add(new SimulatedTagConfig { Tag = "sim.newTag", Waveform = "sine", Max = 100 });
        Vm.MarkDirty();
    }

    private void OnDeleteSimTag(object sender, RoutedEventArgs e)
    {
        foreach (var item in SimTagGrid.SelectedItems.Cast<SimulatedTagConfig>().ToList())
            Vm.Config.Simulation.Tags.Remove(item);
        Vm.MarkDirty();
    }

    // ---- Shared helpers -------------------------------------------------------------------

    private static PointConfig NewPoint(ModbusArea area, int offset, AccessMode access)
    {
        var isBitArea = area is ModbusArea.Coil or ModbusArea.DiscreteInput;
        return new PointConfig
        {
            Tag = "new.tag",
            Offset = offset,
            DataType = isBitArea ? PointDataType.Bool : PointDataType.UInt16,
            BitIndex = -1,
            Access = access,
            Scale = new ScalingConfig()
        };
    }

    private static int NextFreeOffset(ServerBlockConfig block) =>
        block.Points.Count == 0 ? 0 : block.Points.Max(p => p.Offset + p.Size);

    private void DeleteSelectedPoints(DataGrid grid)
    {
        if (grid.ItemsSource is not System.Collections.IList list) return;
        foreach (var item in grid.SelectedItems.Cast<object>().ToList()) list.Remove(item);
        Vm.MarkDirty();
    }

    /// <summary>
    /// Generates a numbered run of points in one step - the thing you actually want when wiring
    /// 60 buttons or 12 gauges, instead of adding rows one at a time.
    /// </summary>
    private void RunAutoFill(IList<PointConfig> destination, ModbusArea area, int span, AccessMode access)
    {
        var dialog = new AutoFillWindow(area, span, access) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        foreach (var point in dialog.Generate()) destination.Add(point);
        Vm.MarkDirty();
    }
}
