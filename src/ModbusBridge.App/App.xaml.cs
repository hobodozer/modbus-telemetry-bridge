using System.IO;
using System.Windows;
using ModbusBridge.App.Diagnostics;
using ModbusBridge.App.ViewModels;
using ModbusBridge.Core.Config;
using ModbusBridge.Core.Diagnostics;

namespace ModbusBridge.App;

public partial class App : Application
{
    private ConfigService? _configService;
    private MainViewModel? _viewModel;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("app", "Unhandled UI exception", args.Exception);
            MessageBox.Show(args.Exception.ToString(), "Unexpected error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var dataDirectory = ResolveDataDirectory(e.Args);
        var configPath = ResolveConfigPath(e.Args, dataDirectory);

        Log.LogDirectory = Path.Combine(dataDirectory, "logs");
        Log.WriteToFile = true;
        Log.Info("app", $"Modbus Telemetry Bridge starting. Data directory: {dataDirectory}");

        _configService = new ConfigService(configPath);

        BridgeConfig config;
        try
        {
            config = _configService.Load();
        }
        catch (Exception ex)
        {
            Log.Error("app", $"Could not load {configPath}", ex);
            var result = MessageBox.Show(
                $"The configuration at\n{configPath}\n\ncould not be loaded:\n\n{ex.Message}\n\n" +
                "Start with a fresh default configuration instead? Your existing file will be left untouched.",
                "Configuration error", MessageBoxButton.YesNo, MessageBoxImage.Error);

            if (result != MessageBoxResult.Yes)
            {
                Shutdown(1);
                return;
            }
            config = DefaultConfig.Create();
        }

        Log.MinimumLevel = config.General.LogLevel;
        Log.WriteToFile = config.General.WriteLogFile;
        Log.PruneOldFiles(config.General.LogRetentionDays);

        _viewModel = new MainViewModel(_configService, config);

        // A file edited in a text editor is picked up without restarting the bridge.
        _configService.ExternallyChanged += reloaded =>
            Dispatcher.BeginInvoke(async () => await _viewModel.AdoptAsync(reloaded));
        _configService.StartWatching();

        // Self-test walks every tab so each template is realised, then reports binding errors and
        // exits. It is how the UI gets verified without anyone having to click through it.
        var selfTest = e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        if (selfTest) BindingErrorListener.Install();

        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;

        _tray = new TrayIcon(_viewModel, window);

        if (!config.General.StartMinimized || selfTest) window.Show();
        else Log.Info("app", "Started minimised to the tray.");

        if (config.General.StartEnginesOnLaunch)
            _ = _viewModel.StartAsync();

        if (selfTest) _ = RunSelfTestAsync(window);
    }

    /// <summary>Cycles every tab, waits for data to flow, then exits non-zero if bindings failed.</summary>
    private async Task RunSelfTestAsync(MainWindow window)
    {
        Log.Info("selftest", "Walking every tab to realise its templates...");

        await Task.Delay(2500);
        var tabCount = await window.Dispatcher.InvokeAsync(() => window.SelectAllTabsCount());

        for (var i = 0; i < tabCount; i++)
        {
            var index = i;
            var name = await window.Dispatcher.InvokeAsync(() => window.SelectTab(index));
            await Task.Delay(1200);
            Log.Info("selftest", $"Tab {index + 1}/{tabCount}: {name}");
        }

        // Give the engine a moment more so live values populate the grids.
        await Task.Delay(1500);

        var errors = BindingErrorListener.Current?.Errors ?? Array.Empty<string>();
        if (errors.Count == 0) Log.Info("selftest", "PASS - no binding errors.");
        else Log.Error("selftest", $"FAIL - {errors.Count} distinct binding error(s).");

        var contrast = await window.Dispatcher.InvokeAsync(CheckPopupContrast);

        if (_viewModel is not null) await _viewModel.ShutdownAsync();
        window.AllowClose = true;
        Shutdown(errors.Count == 0 && contrast == 0 ? 0 : 2);
    }

    /// <summary>
    /// Popup-hosted controls - tooltips, dropdown items, context menus - render in their own visual
    /// tree with the SYSTEM's chrome rather than the window's, so a themed app can end up with its
    /// light foreground on a system-white background. That shipped once and was unreadable.
    /// This instantiates each of them and checks the text actually contrasts with what is behind it.
    /// </summary>
    private static int CheckPopupContrast()
    {
        var failures = 0;

        // The surface a popup paints before its content draws on top. A control with a transparent
        // background is not broken - whatever hosts it supplies the colour - so contrast has to be
        // measured against this, not against the transparency.
        var surface = ((System.Windows.Media.SolidColorBrush)Current.Resources["BgPanel"]).Color;

        // The bug this exists for is a popup type having NO style, so it falls back to system
        // white while inheriting the theme's light text. Assert the theme covers each one.
        void HasStyle(Type type)
        {
            if (Current.TryFindResource(type) is Style) return;
            Log.Error("selftest", $"FAIL - no implicit Style for {type.Name}; it will render with " +
                                  "system colours inside its popup.");
            failures++;
        }

        void Check(string what, FrameworkElement element, System.Windows.Media.Color behind)
        {
            element.Measure(new System.Windows.Size(400, 200));

            var backBrush = element.GetValue(System.Windows.Controls.Control.BackgroundProperty)
                as System.Windows.Media.SolidColorBrush;
            var foreBrush = element.GetValue(System.Windows.Controls.Control.ForegroundProperty)
                as System.Windows.Media.SolidColorBrush;

            if (foreBrush is null)
            {
                Log.Error("selftest", $"FAIL - {what} has no explicit foreground brush.");
                failures++;
                return;
            }

            // Transparent (or unset) means the host paints it; measure against that instead.
            var background = backBrush is null || backBrush.Color.A == 0 ? behind : backBrush.Color;

            var ratio = ContrastRatio(background, foreBrush.Color);
            if (ratio < 4.5)
            {
                Log.Error("selftest", $"FAIL - {what} contrast {ratio:0.0}:1 " +
                                      $"(bg {background}, fg {foreBrush.Color}) is below 4.5:1.");
                failures++;
            }
            else
            {
                Log.Info("selftest", $"{what} contrast {ratio:0.0}:1 - readable.");
            }
        }

        HasStyle(typeof(System.Windows.Controls.ToolTip));
        HasStyle(typeof(System.Windows.Controls.ComboBoxItem));
        HasStyle(typeof(System.Windows.Controls.ContextMenu));
        HasStyle(typeof(System.Windows.Controls.MenuItem));

        Check("ToolTip", new System.Windows.Controls.ToolTip { Content = "sample" }, surface);
        Check("ComboBoxItem", new System.Windows.Controls.ComboBoxItem { Content = "sample" }, surface);

        // A MenuItem is transparent by design and sits on the ContextMenu's background, so it is
        // checked against the menu surface rather than its own brush.
        Check("MenuItem", new System.Windows.Controls.MenuItem { Header = "sample" }, surface);

        // Property checks cannot see a template that IGNORES the properties - the stock ComboBox
        // paints its closed box from system chrome and leaves Background unused, which reads as a
        // white box however the style is written. So render these for real and look at the pixels.
        failures += CheckRendered("ComboBox (closed box)", MakeComboBox(), 160, 26);
        failures += CheckRendered("TextBox", new System.Windows.Controls.TextBox { Text = "sample" }, 160, 26);

        if (failures == 0) Log.Info("selftest", "PASS - popup text contrasts with its background.");
        return failures;
    }

    private static System.Windows.Controls.ComboBox MakeComboBox()
    {
        var combo = new System.Windows.Controls.ComboBox { ItemsSource = new[] { "sample", "other" } };
        combo.SelectedIndex = 0;
        return combo;
    }

    /// <summary>
    /// Renders a control offscreen and checks the surface it actually paints is dark. This is the
    /// only way to catch a control template that ignores the Background it was given.
    /// </summary>
    private static int CheckRendered(string what, FrameworkElement element, int width, int height)
    {
        element.Width = width;
        element.Height = height;

        // A control that has never been connected to a presentation source never builds its
        // template chrome, and renders as empty transparency. Host it in a window parked
        // offscreen at zero opacity so it lays out and paints for real without being seen.
        var host = new Window
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            AllowsTransparency = true,
            Opacity = 0,
            Left = -10000,
            Top = -10000,
            Width = width,
            Height = height,
            Content = element
        };

        byte[] pixels;
        try
        {
            host.Show();
            element.UpdateLayout();

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(element);

            pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
        }
        finally
        {
            host.Content = null;
            host.Close();
        }

        // The most common colour is the fill behind the text.
        var counts = new Dictionary<uint, int>();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < 8) continue;   // ignore fully transparent corners
            var key = (uint)(pixels[i + 2] << 16 | pixels[i + 1] << 8 | pixels[i]);
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        if (counts.Count == 0)
        {
            Log.Error("selftest", $"FAIL - {what} rendered nothing opaque.");
            return 1;
        }

        var dominant = counts.OrderByDescending(p => p.Value).First().Key;
        var colour = System.Windows.Media.Color.FromRgb(
            (byte)(dominant >> 16), (byte)(dominant >> 8), (byte)dominant);

        // Against this theme's lightest text (#E4E7EC); a pale surface fails outright.
        var ratio = ContrastRatio(colour, System.Windows.Media.Color.FromRgb(0xE4, 0xE7, 0xEC));
        if (ratio < 4.5)
        {
            Log.Error("selftest", $"FAIL - {what} paints {colour}, which gives only {ratio:0.0}:1 " +
                                  "against this theme's text. Its template is probably ignoring Background.");
            return 1;
        }

        Log.Info("selftest", $"{what} paints {colour} - {ratio:0.0}:1 against theme text.");
        return 0;
    }

    /// <summary>WCAG relative-luminance contrast ratio, 1:1 (identical) to 21:1 (black on white).</summary>
    private static double ContrastRatio(System.Windows.Media.Color a, System.Windows.Media.Color b)
    {
        static double Luminance(System.Windows.Media.Color c)
        {
            static double Channel(byte v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        var (high, low) = (Luminance(a), Luminance(b));
        if (high < low) (high, low) = (low, high);
        return (high + 0.05) / (low + 0.05);
    }

    /// <summary>
    /// Portable by default: everything lives beside the executable. Falls back to LocalAppData when
    /// the install folder is read-only, so running from Program Files still works.
    /// </summary>
    private static string ResolveDataDirectory(string[] args)
    {
        var overridden = ReadArgument(args, "--data");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            Directory.CreateDirectory(overridden);
            return Path.GetFullPath(overridden);
        }

        var beside = AppContext.BaseDirectory;
        if (IsWritable(beside)) return beside;

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModbusBridge");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static string ResolveConfigPath(string[] args, string dataDirectory)
    {
        var overridden = ReadArgument(args, "--config");
        return !string.IsNullOrWhiteSpace(overridden)
            ? Path.GetFullPath(overridden)
            : Path.Combine(dataDirectory, "config", "bridge.json");
    }

    private static string? ReadArgument(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _configService?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Stops the engine cleanly, then quits.</summary>
    public async Task ShutdownGracefullyAsync()
    {
        if (_viewModel is not null) await _viewModel.ShutdownAsync();
        if (MainWindow is MainWindow window) window.AllowClose = true;
        Shutdown();
    }
}
