using Avalonia;

namespace ModbusBridge.AvaloniaApp;

internal static class Program
{
    // Avalonia needs this before any Avalonia type is touched, so it must stay a plain Main.
    [STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .LogToTrace();
}
