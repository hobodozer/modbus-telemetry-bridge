using System.Buffers.Binary;
using System.Text;

namespace ModbusBridge.Core.Data;

/// <summary>
/// Converts between raw Modbus registers and numeric values, honouring per-device word and byte
/// order. Registers arrive here already in host order (the MBAP layer has undone wire endianness);
/// <see cref="ByteOrder.Swapped"/> covers devices that additionally swap bytes inside a register.
/// </summary>
public static class ValueCodec
{
    public static int RegisterCount(PointDataType type, int length = 0) => type switch
    {
        PointDataType.Bool or PointDataType.Int16 or PointDataType.UInt16 => 1,
        PointDataType.Int32 or PointDataType.UInt32 or PointDataType.Float32 => 2,
        PointDataType.Int64 or PointDataType.UInt64 or PointDataType.Float64 => 4,
        PointDataType.String => Math.Max(1, length),
        _ => 1
    };

    private static ushort ApplyByteOrder(ushort register, ByteOrder order) =>
        order == ByteOrder.Swapped ? BinaryPrimitives.ReverseEndianness(register) : register;

    /// <summary>Reads registers into a big-endian byte buffer with word order applied.</summary>
    private static void Gather(ReadOnlySpan<ushort> registers, Span<byte> destination,
                               WordOrder wordOrder, ByteOrder byteOrder)
    {
        var count = destination.Length / 2;
        for (var i = 0; i < count; i++)
        {
            var sourceIndex = wordOrder == WordOrder.HighFirst ? i : count - 1 - i;
            var word = ApplyByteOrder(registers[sourceIndex], byteOrder);
            BinaryPrimitives.WriteUInt16BigEndian(destination[(i * 2)..], word);
        }
    }

    private static void Scatter(ReadOnlySpan<byte> source, Span<ushort> registers,
                                WordOrder wordOrder, ByteOrder byteOrder)
    {
        var count = source.Length / 2;
        for (var i = 0; i < count; i++)
        {
            var word = BinaryPrimitives.ReadUInt16BigEndian(source[(i * 2)..]);
            var targetIndex = wordOrder == WordOrder.HighFirst ? i : count - 1 - i;
            registers[targetIndex] = ApplyByteOrder(word, byteOrder);
        }
    }

    /// <summary>Decodes a numeric point. <paramref name="registers"/> must be at least the point's size.</summary>
    public static double Decode(ReadOnlySpan<ushort> registers, PointDataType type,
                                WordOrder wordOrder, ByteOrder byteOrder, int bitIndex = -1)
    {
        switch (type)
        {
            case PointDataType.Bool:
            {
                var word = ApplyByteOrder(registers[0], byteOrder);
                var bit = bitIndex < 0 ? 0 : bitIndex;
                return (word >> bit & 1) != 0 ? 1d : 0d;
            }
            case PointDataType.Int16:
                return (short)ApplyByteOrder(registers[0], byteOrder);
            case PointDataType.UInt16:
                return ApplyByteOrder(registers[0], byteOrder);
        }

        Span<byte> buffer = stackalloc byte[8];
        var size = RegisterCount(type) * 2;
        var slice = buffer[..size];
        Gather(registers, slice, wordOrder, byteOrder);

        return type switch
        {
            PointDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(slice),
            PointDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(slice),
            PointDataType.Int64 => BinaryPrimitives.ReadInt64BigEndian(slice),
            PointDataType.UInt64 => BinaryPrimitives.ReadUInt64BigEndian(slice),
            PointDataType.Float32 => BinaryPrimitives.ReadSingleBigEndian(slice),
            PointDataType.Float64 => BinaryPrimitives.ReadDoubleBigEndian(slice),
            _ => 0d
        };
    }

    /// <summary>
    /// Encodes a numeric point into <paramref name="registers"/>. For a packed Bool the caller must
    /// pass the register that already holds the other bits - only the target bit is modified.
    /// </summary>
    public static void Encode(double value, Span<ushort> registers, PointDataType type,
                              WordOrder wordOrder, ByteOrder byteOrder, int bitIndex = -1)
    {
        switch (type)
        {
            case PointDataType.Bool:
            {
                var bit = bitIndex < 0 ? 0 : bitIndex;
                var word = ApplyByteOrder(registers[0], byteOrder);
                word = value != 0d
                    ? (ushort)(word | 1 << bit)
                    : (ushort)(word & ~(1 << bit));
                registers[0] = ApplyByteOrder(word, byteOrder);
                return;
            }
            case PointDataType.Int16:
                registers[0] = ApplyByteOrder((ushort)(short)ClampToRange(value, short.MinValue, short.MaxValue), byteOrder);
                return;
            case PointDataType.UInt16:
                registers[0] = ApplyByteOrder((ushort)ClampToRange(value, ushort.MinValue, ushort.MaxValue), byteOrder);
                return;
        }

        Span<byte> buffer = stackalloc byte[8];
        var size = RegisterCount(type) * 2;
        var slice = buffer[..size];

        switch (type)
        {
            case PointDataType.Int32:
                BinaryPrimitives.WriteInt32BigEndian(slice, (int)ClampToRange(value, int.MinValue, int.MaxValue));
                break;
            case PointDataType.UInt32:
                BinaryPrimitives.WriteUInt32BigEndian(slice, (uint)ClampToRange(value, uint.MinValue, uint.MaxValue));
                break;
            case PointDataType.Int64:
                BinaryPrimitives.WriteInt64BigEndian(slice, (long)ClampToRange(value, long.MinValue, long.MaxValue));
                break;
            case PointDataType.UInt64:
                BinaryPrimitives.WriteUInt64BigEndian(slice, (ulong)ClampToRange(value, ulong.MinValue, ulong.MaxValue));
                break;
            case PointDataType.Float32:
                BinaryPrimitives.WriteSingleBigEndian(slice, (float)value);
                break;
            case PointDataType.Float64:
                BinaryPrimitives.WriteDoubleBigEndian(slice, value);
                break;
            default:
                return;
        }

        Scatter(slice, registers, wordOrder, byteOrder);
    }

    /// <summary>Decodes an ASCII string packed two characters per register, high byte first.</summary>
    public static string DecodeString(ReadOnlySpan<ushort> registers, int registerCount, ByteOrder byteOrder)
    {
        var builder = new StringBuilder(registerCount * 2);
        for (var i = 0; i < registerCount && i < registers.Length; i++)
        {
            var word = ApplyByteOrder(registers[i], byteOrder);
            var high = (char)(word >> 8);
            var low = (char)(word & 0xFF);
            if (high == '\0') break;
            builder.Append(high);
            if (low == '\0') break;
            builder.Append(low);
        }
        return builder.ToString();
    }

    public static void EncodeString(string? text, Span<ushort> registers, int registerCount, ByteOrder byteOrder)
    {
        text ??= "";
        for (var i = 0; i < registerCount && i < registers.Length; i++)
        {
            var high = i * 2 < text.Length ? (byte)text[i * 2] : (byte)0;
            var low = i * 2 + 1 < text.Length ? (byte)text[i * 2 + 1] : (byte)0;
            registers[i] = ApplyByteOrder((ushort)(high << 8 | low), byteOrder);
        }
    }

    private static double ClampToRange(double value, double min, double max)
    {
        if (double.IsNaN(value)) return 0d;
        return Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), min, max);
    }
}
