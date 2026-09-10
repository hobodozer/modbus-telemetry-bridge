using ModbusBridge.Core.Data;

namespace ModbusBridge.Core.Tags;

/// <summary>
/// One point in the tag database. Readers observe <see cref="Version"/> to detect changes without
/// subscribing to events - every sink polls at whatever rate it needs and only does work when the
/// version it last saw has moved.
/// </summary>
public sealed class TagEntry
{
    private TagValue _value = TagValue.Unset;
    private long _version;

    public TagEntry(string name)
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>Free-form description shown in the UI. Set from config when the tag is declared.</summary>
    public string? Description { get; set; }

    /// <summary>Engineering units shown in the UI, e.g. "km/h". Purely cosmetic.</summary>
    public string? Units { get; set; }

    /// <summary>Declared type, used for display formatting and for auto-created server points.</summary>
    public PointDataType DataType { get; set; } = PointDataType.Float64;

    /// <summary>Id of the component that last wrote this tag. Used to suppress write-back loops.</summary>
    public string? LastWriterId { get; private set; }

    /// <summary>Whether a value was ever forced from the UI. Forced tags ignore source updates.</summary>
    public bool IsForced { get; private set; }

    /// <summary>Monotonic counter bumped on every accepted write.</summary>
    public long Version => Interlocked.Read(ref _version);

    public TagValue Value => Volatile.Read(ref _value);

    /// <summary>
    /// Publishes a new value. Returns false when the write was suppressed because the tag is forced.
    /// </summary>
    public bool Set(TagValue value, string? writerId = null)
    {
        if (IsForced) return false;
        SetInternal(value, writerId);
        return true;
    }

    /// <summary>Pins the tag to a value from the UI; source updates are ignored until <see cref="Unforce"/>.</summary>
    public void Force(TagValue value)
    {
        IsForced = true;
        SetInternal(value, "force");
    }

    public void Unforce() => IsForced = false;

    /// <summary>Downgrades quality in place (used by the staleness sweeper) without touching the value.</summary>
    public void DemoteQuality(TagQuality quality)
    {
        if (IsForced) return;
        var current = Volatile.Read(ref _value);
        if (current.Quality <= quality) return;
        SetInternal(current.WithQuality(quality), LastWriterId);
    }

    private void SetInternal(TagValue value, string? writerId)
    {
        LastWriterId = writerId;
        Volatile.Write(ref _value, value);
        Interlocked.Increment(ref _version);
    }
}
