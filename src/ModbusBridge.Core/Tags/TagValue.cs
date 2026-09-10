using ModbusBridge.Core.Data;

namespace ModbusBridge.Core.Tags;

/// <summary>
/// An immutable snapshot of a tag. Numeric values are carried as <see cref="double"/>, which
/// represents every integer type up to 2^53 and all Float32 values exactly; string tags carry
/// <see cref="Text"/> instead. Booleans are 0 / 1.
/// </summary>
public sealed class TagValue
{
    public static readonly TagValue Unset = new(0d, null, TagQuality.Never, DateTime.MinValue);

    public double Number { get; }
    public string? Text { get; }
    public TagQuality Quality { get; }
    public DateTime TimestampUtc { get; }

    public TagValue(double number, string? text, TagQuality quality, DateTime timestampUtc)
    {
        Number = number;
        Text = text;
        Quality = quality;
        TimestampUtc = timestampUtc;
    }

    public static TagValue Good(double number) => new(number, null, TagQuality.Good, DateTime.UtcNow);
    public static TagValue Good(bool value) => new(value ? 1d : 0d, null, TagQuality.Good, DateTime.UtcNow);
    public static TagValue GoodText(string text) => new(0d, text, TagQuality.Good, DateTime.UtcNow);

    public bool Bool => Number != 0d;

    public TagValue WithQuality(TagQuality quality) =>
        quality == Quality ? this : new TagValue(Number, Text, quality, TimestampUtc);

    public override string ToString() => Text ?? Number.ToString("0.######");
}
