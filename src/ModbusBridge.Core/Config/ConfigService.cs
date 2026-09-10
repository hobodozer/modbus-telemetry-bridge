using System.Text.Json;
using System.Text.Json.Serialization;
using ModbusBridge.Core.Diagnostics;

namespace ModbusBridge.Core.Config;

/// <summary>
/// Loads, saves, backs up and watches the JSON configuration. Everything is portable-relative:
/// the config lives next to the executable unless an explicit path is given, so the whole folder
/// can be copied to another machine or a USB stick.
/// </summary>
public sealed class ConfigService : IDisposable
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private FileSystemWatcher? _watcher;
    private DateTime _lastSelfWriteUtc;
    private readonly object _gate = new();

    public ConfigService(string configPath)
    {
        ConfigPath = Path.GetFullPath(configPath);
        BackupDirectory = Path.Combine(Path.GetDirectoryName(ConfigPath)!, "backups");
    }

    public string ConfigPath { get; }
    public string BackupDirectory { get; }

    /// <summary>Raised when the file changed on disk from outside this process.</summary>
    public event Action<BridgeConfig>? ExternallyChanged;

    public static JsonSerializerOptions SerializerOptions => Options;

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true));
        return options;
    }

    public bool Exists => File.Exists(ConfigPath);

    public BridgeConfig Load()
    {
        lock (_gate)
        {
            if (!File.Exists(ConfigPath))
            {
                Log.Info("config", $"No config at {ConfigPath}; creating a default one.");
                var fresh = DefaultConfig.Create();
                SaveInternal(fresh, backup: false);
                return fresh;
            }

            var json = ReadWithRetry(ConfigPath);
            var config = JsonSerializer.Deserialize<BridgeConfig>(json, Options)
                         ?? throw new InvalidDataException("Configuration file is empty or malformed.");

            if (config.Version > BridgeConfig.CurrentVersion)
                Log.Warn("config", $"Config version {config.Version} is newer than this build supports " +
                                   $"({BridgeConfig.CurrentVersion}); unknown settings will be dropped on save.");

            config.Version = BridgeConfig.CurrentVersion;
            return config;
        }
    }

    public void Save(BridgeConfig config) => SaveInternal(config, backup: true);

    private void SaveInternal(BridgeConfig config, bool backup)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            if (backup && File.Exists(ConfigPath)) WriteBackup();

            // Write to a temp file then move, so a crash mid-write cannot corrupt the live config.
            var temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(config, Options));
            _lastSelfWriteUtc = DateTime.UtcNow;
            File.Move(temp, ConfigPath, overwrite: true);
            Log.Debug("config", $"Saved {ConfigPath}");
        }
    }

    private void WriteBackup()
    {
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            var name = $"{Path.GetFileNameWithoutExtension(ConfigPath)}-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            File.Copy(ConfigPath, Path.Combine(BackupDirectory, name), overwrite: true);

            // Keep the 20 most recent backups.
            var stale = Directory.EnumerateFiles(BackupDirectory, "*.json")
                                 .OrderByDescending(File.GetLastWriteTimeUtc)
                                 .Skip(20);
            foreach (var file in stale) File.Delete(file);
        }
        catch (Exception ex)
        {
            Log.Warn("config", $"Could not write config backup: {ex.Message}");
        }
    }

    /// <summary>Starts watching the file so edits made in a text editor are picked up live.</summary>
    public void StartWatching()
    {
        if (_watcher is not null) return;
        var directory = Path.GetDirectoryName(ConfigPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

        _watcher = new FileSystemWatcher(directory, Path.GetFileName(ConfigPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
    }

    public void StopWatching()
    {
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFileChanged;
        _watcher.Created -= OnFileChanged;
        _watcher.Renamed -= OnFileChanged;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Ignore the echo of our own save, and coalesce the burst of events a single save produces.
        if ((DateTime.UtcNow - _lastSelfWriteUtc).TotalMilliseconds < 1500) return;

        Task.Run(async () =>
        {
            await Task.Delay(300).ConfigureAwait(false);
            try
            {
                var config = Load();
                var problems = config.Validate();
                if (problems.Count > 0)
                {
                    Log.Warn("config", $"Reloaded config has {problems.Count} problem(s); not applying. " +
                                       $"First: {problems[0]}");
                    return;
                }
                Log.Info("config", "Configuration file changed on disk; reloading.");
                ExternallyChanged?.Invoke(config);
            }
            catch (Exception ex)
            {
                Log.Error("config", "Failed to reload changed config", ex);
            }
        });
    }

    private static string ReadWithRetry(string path)
    {
        // An editor may still hold the file for a moment after saving.
        for (var attempt = 0; ; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    public static string Serialize(BridgeConfig config) => JsonSerializer.Serialize(config, Options);

    public static BridgeConfig Deserialize(string json) =>
        JsonSerializer.Deserialize<BridgeConfig>(json, Options)
        ?? throw new InvalidDataException("Configuration is empty or malformed.");

    public void Dispose() => StopWatching();
}
