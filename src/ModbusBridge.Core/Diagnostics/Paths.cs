namespace ModbusBridge.Core.Diagnostics;

/// <summary>
/// Where this instance keeps its files. The app sets it at startup from --data (or from beside the
/// executable), and Core resolves relative paths in configuration against it.
///
/// Without this a relative path in config lands wherever the process happens to have been started
/// from, which is neither predictable nor what the config says it does.
/// </summary>
public static class Paths
{
    public static string DataDirectory { get; set; } = AppContext.BaseDirectory;

    /// <summary>Resolves a configured path, leaving absolute ones alone.</summary>
    public static string Resolve(string path, string fallback)
    {
        if (string.IsNullOrWhiteSpace(path)) path = fallback;
        return System.IO.Path.IsPathRooted(path)
            ? path
            : System.IO.Path.Combine(DataDirectory, path);
    }
}
