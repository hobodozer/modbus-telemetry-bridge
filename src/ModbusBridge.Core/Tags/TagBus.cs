using System.Collections.Concurrent;
using ModbusBridge.Core.Data;

namespace ModbusBridge.Core.Tags;

/// <summary>
/// The in-memory point database every source and sink talks to. Sources (Modbus poller, SimHub,
/// PC sensors) publish tags; sinks (Modbus server, vJoy, PLC write groups, logging) resolve the
/// tags they care about once and then poll <see cref="TagEntry.Version"/>.
/// Tag names are case-insensitive.
/// </summary>
public sealed class TagBus
{
    private readonly ConcurrentDictionary<string, TagEntry> _tags =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bumped whenever a tag is created, so the UI can refresh its list cheaply.</summary>
    public long StructureVersion => Interlocked.Read(ref _structureVersion);
    private long _structureVersion;

    /// <summary>Gets an existing tag or creates it. Mappings auto-create tags they reference.</summary>
    public TagEntry GetOrAdd(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tag name must not be empty.", nameof(name));

        var trimmed = name.Trim();
        if (_tags.TryGetValue(trimmed, out var existing)) return existing;

        var created = _tags.GetOrAdd(trimmed, static n => new TagEntry(n));
        Interlocked.Increment(ref _structureVersion);
        return created;
    }

    public TagEntry? Find(string name) =>
        _tags.TryGetValue(name.Trim(), out var entry) ? entry : null;

    public bool Remove(string name)
    {
        var removed = _tags.TryRemove(name.Trim(), out _);
        if (removed) Interlocked.Increment(ref _structureVersion);
        return removed;
    }

    public IReadOnlyCollection<TagEntry> All => _tags.Values.ToArray();

    public int Count => _tags.Count;

    public IEnumerable<TagEntry> Snapshot() => _tags.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase);

    public void Set(string name, double value, string? writerId = null) =>
        GetOrAdd(name).Set(TagValue.Good(value), writerId);

    public void Set(string name, bool value, string? writerId = null) =>
        GetOrAdd(name).Set(TagValue.Good(value), writerId);

    public double GetNumber(string name, double fallback = 0d) =>
        Find(name) is { } t && t.Value.Quality > TagQuality.Bad ? t.Value.Number : fallback;

    /// <summary>
    /// Marks every tag last written by <paramref name="writerId"/> as bad - used when a device
    /// drops so consumers can distinguish "offline" from "value happens to be zero".
    /// </summary>
    public void MarkSourceBad(string writerId)
    {
        foreach (var tag in _tags.Values)
        {
            if (string.Equals(tag.LastWriterId, writerId, StringComparison.Ordinal))
                tag.DemoteQuality(TagQuality.Bad);
        }
    }

    /// <summary>Demotes Good tags to Stale once they exceed <paramref name="staleAfter"/>.</summary>
    public void SweepStale(TimeSpan staleAfter)
    {
        if (staleAfter <= TimeSpan.Zero) return;
        var cutoff = DateTime.UtcNow - staleAfter;
        foreach (var tag in _tags.Values)
        {
            var value = tag.Value;
            if (value.Quality == TagQuality.Good && value.TimestampUtc < cutoff)
                tag.DemoteQuality(TagQuality.Stale);
        }
    }
}
