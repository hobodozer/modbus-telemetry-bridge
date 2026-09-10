using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ModbusBridge.AvaloniaApp;

public partial class GridSpikeWindow : Window
{
    public GridSpikeWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = new GridSpikeViewModel();
    }
}
