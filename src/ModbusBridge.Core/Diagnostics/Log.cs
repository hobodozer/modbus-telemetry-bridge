using System.Collections.Concurrent;
using System.Text;
using ModbusBridge.Core.Data;

namespace ModbusBridge.Core.Diagnostics;

public sealed record LogEntry(DateTime TimestampLocal, LogLevel Level, string Source, string Message);

/// <summary>
/// Process-wide log: a bounded ring buffer the UI reads, plus an optional daily file.
/// Deliberately tiny - no external logging dependency so the published exe stays self-contained.
/// </summary>
public static class Log
{
    private const int RingCapacity = 5000;

    private static readonly ConcurrentQueue<LogEntry> Ring = new();
    private static readonly object FileLock = new();

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;
    public static bool WriteToFile { get; set; }
    public static string LogDirectory { get; set; } = "";

    /// <summary>Raised for every accepted entry. Handlers must not block.</summary>
    public static event Action<LogEntry>? Entry;

    public static void Trace(string source, string message) => Write(LogLevel.Trace, source, message);
    public static void Debug(string source, string message) => Write(LogLevel.Debug, source, message);
    public static void Info(string source, string message) => Write(LogLevel.Info, source, message);
    public static void Warn(string source, string message) => Write(LogLevel.Warn, source, message);
    public static void Error(string source, string message) => Write(LogLevel.Error, source, message);

    public static void Error(string source, string message, Exception ex) =>
        Write(LogLevel.Error, source, $"{message}: {ex.GetType().Name} {ex.Message}");

    public static void Write(LogLevel level, string source, string message)
    {
        if (level < MinimumLevel) return;

        var entry = new LogEntry(DateTime.Now, level, source, message);

        Ring.Enqueue(entry);
        while (Ring.Count > RingCapacity) Ring.TryDequeue(out _);

        if (WriteToFile) AppendToFile(entry);

        try { Entry?.Invoke(entry); }
        catch { /* a broken log subscriber must never take down a device loop */ }
    }

    public static IReadOnlyList<LogEntry> Recent(int count = 500) =>
        Ring.ToArray().TakeLast(count).ToArray();

    public static void Clear()
    {
        while (Ring.TryDequeue(out _)) { }
    }

    private static void AppendToFile(LogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(LogDirectory)) return;
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, $"bridge-{entry.TimestampLocal:yyyy-MM-dd}.log");
                var line = new StringBuilder()
                    .Append(entry.TimestampLocal.ToString("HH:mm:ss.fff"))
                    .Append(' ').Append(entry.Level.ToString().ToUpperInvariant().PadRight(5))
                    .Append(" [").Append(entry.Source).Append("] ")
                    .Append(entry.Message)
                    .AppendLine();
                File.AppendAllText(path, line.ToString());
            }
        }
        catch
        {
            // Never let logging failures propagate into the data path.
        }
    }

    /// <summary>Deletes bridge-*.log files older than <paramref name="retentionDays"/>.</summary>
    public static void PruneOldFiles(int retentionDays)
    {
        if (retentionDays <= 0 || string.IsNullOrWhiteSpace(LogDirectory)) return;
        try
        {
            if (!Directory.Exists(LogDirectory)) return;
            var cutoff = DateTime.Now.AddDays(-retentionDays);
            foreach (var file in Directory.EnumerateFiles(LogDirectory, "bridge-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
