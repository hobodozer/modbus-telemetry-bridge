using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using ModbusBridge.App.ViewModels;
using Forms = System.Windows.Forms;

namespace ModbusBridge.App;

/// <summary>
/// Tray presence so the bridge can run out of the way during a session. Uses WinForms' NotifyIcon
/// (WPF has no equivalent) and draws its own icon, so there is no external asset to ship.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly MainViewModel _viewModel;
    private readonly Window _window;
    private readonly Icon _online;
    private readonly Icon _offline;
    private bool _lastRunning;

    public TrayIcon(MainViewModel viewModel, Window window)
    {
        _viewModel = viewModel;
        _window = window;

        _online = BuildIcon(Color.FromArgb(63, 182, 94));
        _offline = BuildIcon(Color.FromArgb(120, 128, 140));

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Start engine", null, async (_, _) => await _viewModel.StartAsync());
        menu.Items.Add("Stop engine", null, async (_, _) => await _viewModel.StopAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) =>
        {
            if (Application.Current is App app) await app.ShutdownGracefullyAsync();
            else Application.Current?.Shutdown();
        });

        _icon = new Forms.NotifyIcon
        {
            Icon = _offline,
            Visible = true,
            Text = "Modbus Telemetry Bridge",
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ShowWindow();

        _window.StateChanged += (_, _) =>
        {
            if (_window.WindowState == WindowState.Minimized) _window.Hide();
        };

        // Poll the engine state rather than subscribing, so the tray never blocks a device loop.
        var timer = new Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) => UpdateState();
        timer.Start();
    }

    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void UpdateState()
    {
        var running = _viewModel.IsRunning;
        if (running == _lastRunning) return;
        _lastRunning = running;

        _icon.Icon = running ? _online : _offline;

        var devices = _viewModel.DeviceStatuses.Count;
        var clients = _viewModel.ServerStatuses.Sum(s => s.ClientCount);
        _icon.Text = running
            ? $"Modbus Bridge - running, {devices} device(s), {clients} client(s)"
            : "Modbus Bridge - stopped";
    }

    /// <summary>Draws a simple filled circle so no .ico file has to ship alongside the exe.</summary>
    private static Icon BuildIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var body = new SolidBrush(Color.FromArgb(35, 39, 46));
            graphics.FillEllipse(body, 1, 1, 30, 30);
            using var ring = new Pen(color, 3f);
            graphics.DrawEllipse(ring, 3, 3, 26, 26);
            using var dot = new SolidBrush(color);
            graphics.FillEllipse(dot, 11, 11, 10, 10);
        }

        var handle = bitmap.GetHicon();
        using var temporary = Icon.FromHandle(handle);
        // Clone so the icon survives destroying the HICON we just created.
        return (Icon)temporary.Clone();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _online.Dispose();
        _offline.Dispose();
    }
}
