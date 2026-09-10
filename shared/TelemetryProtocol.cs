// Shared by the .NET 8 bridge and the .NET Framework 4.8 SimHub plugin. Both projects compile this
// same file via a <Compile Include> link, so the two ends of the wire can never drift apart.
//
// Deliberately written against plain C# 7 constructs and BitConverter rather than Span /
// BinaryPrimitives: net48 does not ship those without extra packages, and a shared file that needs
// a NuGet reference on one side is a trap.
//
// All values are little-endian, which is the native order on both ends - no byte swapping in the
// hot path. Transport is UDP on the loopback interface.

// Nullable annotations are off here: this file also compiles under net48, where the nullable
// attributes and null-forgiving patterns the bridge uses elsewhere are not worth the friction.
// Every public entry point defends against null explicitly instead.
#nullable disable

using System;
using System.Collections.Generic;
using System.Text;

namespace ModbusBridge.Telemetry
{
    /// <summary>Value kind of a telemetry property.</summary>
    public enum TelemetryValueType : byte
    {
        Number = 0,
        Bool = 1,
        Text = 2
    }

    /// <summary>Message kinds carried in the frame header.</summary>
    public enum TelemetryMessageType : byte
    {
        /// <summary>Plugin to bridge: everything SimHub currently offers, chunked. Drives the property browser.</summary>
        Catalog = 1,

        /// <summary>Plugin to bridge: the ordered set of properties the following data frames carry.</summary>
        Schema = 2,

        /// <summary>Plugin to bridge: one sample of every subscribed property.</summary>
        Data = 3,

        /// <summary>Bridge to plugin: the properties the bridge wants streamed.</summary>
        Subscribe = 4,

        /// <summary>Bridge to plugin: bridge tag values to publish back into SimHub as properties.</summary>
        InputState = 5,

        /// <summary>Plugin to bridge: liveness plus which game is running.</summary>
        Hello = 6,

        /// <summary>Bridge to plugin: asks for a fresh catalog immediately.</summary>
        RequestCatalog = 7
    }

    /// <summary>One property in a catalog or schema.</summary>
    public sealed class TelemetryProperty
    {
        public TelemetryProperty(string name, TelemetryValueType type)
        {
            Name = name;
            Type = type;
        }

        public string Name { get; }
        public TelemetryValueType Type { get; }

        public override string ToString() => Name + " (" + Type + ")";
    }

    /// <summary>One decoded sample.</summary>
    public sealed class TelemetrySample
    {
        public uint SchemaId;
        public uint Sequence;

        /// <summary>Plugin-side timestamp in milliseconds, used to measure one-way latency.</summary>
        public long TimestampMs;

        public double[] Numbers = new double[0];
        public string[] Texts = new string[0];
    }

    public static class TelemetryProtocol
    {
        /// <summary>
        /// Wire version we emit. 2 chunked the schema; 1 sent it as a single datagram.
        /// A reader accepts anything up to this, so a bridge keeps working with a plugin that
        /// has not been reinstalled yet - upgrading both ends at once needs elevation, and the
        /// two do not always get restarted together.
        /// </summary>
        public const byte Version = 2;

        /// <summary>Oldest wire version still understood.</summary>
        public const byte MinimumVersion = 1;
        public const int HeaderLength = 8;

        /// <summary>Kept well under the loopback MTU so a catalog chunk never fragments awkwardly.</summary>
        /// <summary>
        /// Biggest datagram either end will build. This is loopback UDP, where the practical
        /// ceiling is the ~65507-byte IPv4 payload limit and the kernel reassembles fragments,
        /// so the old 8 KB was needlessly small: a subscription of a few hundred properties
        /// overflows a schema message, and the component array overflows a data frame.
        /// </summary>
        public const int MaxDatagram = 60000;

        private static readonly byte[] Magic = { (byte)'S', (byte)'H', (byte)'T', (byte)'B' };

        // ---- Header -------------------------------------------------------------------------

        public static int WriteHeader(byte[] buffer, TelemetryMessageType type)
        {
            buffer[0] = Magic[0];
            buffer[1] = Magic[1];
            buffer[2] = Magic[2];
            buffer[3] = Magic[3];
            buffer[4] = Version;
            buffer[5] = (byte)type;
            buffer[6] = 0;
            buffer[7] = 0;
            return HeaderLength;
        }

        /// <summary>Validates the header. Returns false for anything that is not one of our frames.</summary>
        public static bool TryReadHeader(byte[] buffer, int length, out TelemetryMessageType type)
        {
            type = default;
            if (length < HeaderLength) return false;
            if (buffer[0] != Magic[0] || buffer[1] != Magic[1] ||
                buffer[2] != Magic[2] || buffer[3] != Magic[3]) return false;
            if (buffer[4] < MinimumVersion || buffer[4] > Version) return false;

            type = (TelemetryMessageType)buffer[5];
            return true;
        }

        /// <summary>Header read that also reports the sender's wire version.</summary>
        public static bool TryReadHeader(byte[] buffer, int length, out TelemetryMessageType type,
                                         out byte version)
        {
            version = length >= HeaderLength ? buffer[4] : (byte)0;
            return TryReadHeader(buffer, length, out type);
        }

        // ---- Primitive writers --------------------------------------------------------------

        public static void WriteUInt16(byte[] buffer, ref int offset, ushort value)
        {
            buffer[offset++] = (byte)value;
            buffer[offset++] = (byte)(value >> 8);
        }

        public static void WriteUInt32(byte[] buffer, ref int offset, uint value)
        {
            buffer[offset++] = (byte)value;
            buffer[offset++] = (byte)(value >> 8);
            buffer[offset++] = (byte)(value >> 16);
            buffer[offset++] = (byte)(value >> 24);
        }

        public static void WriteInt64(byte[] buffer, ref int offset, long value)
        {
            for (int i = 0; i < 8; i++) buffer[offset++] = (byte)(value >> (i * 8));
        }

        public static void WriteDouble(byte[] buffer, ref int offset, double value)
        {
            var bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, buffer, offset, 8);
            offset += 8;
        }

        /// <summary>Length-prefixed UTF-8, one byte of length. Names longer than 255 bytes are truncated.</summary>
        public static void WriteShortString(byte[] buffer, ref int offset, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            var count = bytes.Length > 255 ? 255 : bytes.Length;
            buffer[offset++] = (byte)count;
            Buffer.BlockCopy(bytes, 0, buffer, offset, count);
            offset += count;
        }

        /// <summary>Length-prefixed UTF-8 with a two-byte length, for free-form text values.</summary>
        public static void WriteString(byte[] buffer, ref int offset, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            var count = bytes.Length > 4096 ? 4096 : bytes.Length;
            WriteUInt16(buffer, ref offset, (ushort)count);
            Buffer.BlockCopy(bytes, 0, buffer, offset, count);
            offset += count;
        }

        // ---- Primitive readers --------------------------------------------------------------

        public static ushort ReadUInt16(byte[] buffer, ref int offset)
        {
            var value = (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
            offset += 2;
            return value;
        }

        public static uint ReadUInt32(byte[] buffer, ref int offset)
        {
            var value = (uint)(buffer[offset] | (buffer[offset + 1] << 8) |
                               (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
            offset += 4;
            return value;
        }

        public static long ReadInt64(byte[] buffer, ref int offset)
        {
            long value = 0;
            for (int i = 0; i < 8; i++) value |= (long)buffer[offset + i] << (i * 8);
            offset += 8;
            return value;
        }

        public static double ReadDouble(byte[] buffer, ref int offset)
        {
            var value = BitConverter.ToDouble(buffer, offset);
            offset += 8;
            return value;
        }

        public static string ReadShortString(byte[] buffer, ref int offset)
        {
            int count = buffer[offset++];
            var value = Encoding.UTF8.GetString(buffer, offset, count);
            offset += count;
            return value;
        }

        public static string ReadString(byte[] buffer, ref int offset)
        {
            int count = ReadUInt16(buffer, ref offset);
            var value = Encoding.UTF8.GetString(buffer, offset, count);
            offset += count;
            return value;
        }

        // ---- Message builders ---------------------------------------------------------------

        /// <summary>
        /// Splits a property catalog across as many datagrams as it takes. SimHub exposes thousands
        /// of properties, so this will not fit in one frame.
        /// </summary>
        public static List<byte[]> BuildCatalog(IList<TelemetryProperty> properties)
        {
            var chunks = new List<List<TelemetryProperty>>();
            var current = new List<TelemetryProperty>();
            var used = HeaderLength + 6;   // chunk index, chunk count, item count

            foreach (var property in properties)
            {
                var size = 2 + Encoding.UTF8.GetByteCount(property.Name);
                if (used + size > MaxDatagram && current.Count > 0)
                {
                    chunks.Add(current);
                    current = new List<TelemetryProperty>();
                    used = HeaderLength + 6;
                }
                current.Add(property);
                used += size;
            }
            if (current.Count > 0) chunks.Add(current);
            if (chunks.Count == 0) chunks.Add(current);

            var datagrams = new List<byte[]>();
            for (int i = 0; i < chunks.Count; i++)
            {
                var buffer = new byte[MaxDatagram];
                var offset = WriteHeader(buffer, TelemetryMessageType.Catalog);
                WriteUInt16(buffer, ref offset, (ushort)i);
                WriteUInt16(buffer, ref offset, (ushort)chunks.Count);
                WriteUInt16(buffer, ref offset, (ushort)chunks[i].Count);

                foreach (var property in chunks[i])
                {
                    buffer[offset++] = (byte)property.Type;
                    WriteShortString(buffer, ref offset, property.Name);
                }

                var datagram = new byte[offset];
                Buffer.BlockCopy(buffer, 0, datagram, 0, offset);
                datagrams.Add(datagram);
            }

            return datagrams;
        }

        public static List<TelemetryProperty> ReadCatalogChunk(byte[] buffer, int length,
                                                               out int chunkIndex, out int chunkCount)
        {
            var offset = HeaderLength;
            chunkIndex = ReadUInt16(buffer, ref offset);
            chunkCount = ReadUInt16(buffer, ref offset);
            int count = ReadUInt16(buffer, ref offset);

            var properties = new List<TelemetryProperty>(count);
            for (int i = 0; i < count && offset < length; i++)
            {
                var type = (TelemetryValueType)buffer[offset++];
                var name = ReadShortString(buffer, ref offset);
                properties.Add(new TelemetryProperty(name, type));
            }
            return properties;
        }

        /// <summary>
        /// Splits a schema across as many datagrams as it takes, the same way the catalog is split.
        /// It used to be a single datagram, which silently truncated - and before the bounds check,
        /// threw inside SimHub's DataUpdate - once a subscription grew past a few hundred
        /// properties. The vehicle-component array alone is 1300.
        /// </summary>
        public static List<byte[]> BuildSchema(uint schemaId, IList<TelemetryProperty> properties)
        {
            var chunks = new List<List<TelemetryProperty>>();
            var current = new List<TelemetryProperty>();
            var used = HeaderLength + 10;   // schema id, chunk index, chunk count, item count

            foreach (var property in properties)
            {
                var size = 1 + 2 + Encoding.UTF8.GetByteCount(property.Name);
                if (used + size > MaxDatagram && current.Count > 0)
                {
                    chunks.Add(current);
                    current = new List<TelemetryProperty>();
                    used = HeaderLength + 10;
                }
                current.Add(property);
                used += size;
            }
            if (current.Count > 0) chunks.Add(current);
            if (chunks.Count == 0) chunks.Add(current);

            var datagrams = new List<byte[]>();
            for (int i = 0; i < chunks.Count; i++)
            {
                var buffer = new byte[MaxDatagram];
                var offset = WriteHeader(buffer, TelemetryMessageType.Schema);
                WriteUInt32(buffer, ref offset, schemaId);
                WriteUInt16(buffer, ref offset, (ushort)i);
                WriteUInt16(buffer, ref offset, (ushort)chunks.Count);
                WriteUInt16(buffer, ref offset, (ushort)chunks[i].Count);

                foreach (var property in chunks[i])
                {
                    buffer[offset++] = (byte)property.Type;
                    WriteShortString(buffer, ref offset, property.Name);
                }

                var datagram = new byte[offset];
                Buffer.BlockCopy(buffer, 0, datagram, 0, offset);
                datagrams.Add(datagram);
            }

            return datagrams;
        }

        public static List<TelemetryProperty> ReadSchema(byte[] buffer, int length, out uint schemaId,
                                                          out int chunkIndex, out int chunkCount,
                                                          int version = Version)
        {
            var offset = HeaderLength;
            schemaId = ReadUInt32(buffer, ref offset);

            // Version 1 had no chunk fields; it was always the whole schema in one datagram.
            if (version < 2)
            {
                chunkIndex = 0;
                chunkCount = 1;
            }
            else
            {
                chunkIndex = ReadUInt16(buffer, ref offset);
                chunkCount = ReadUInt16(buffer, ref offset);
            }
            int count = ReadUInt16(buffer, ref offset);

            var properties = new List<TelemetryProperty>(count);
            for (int i = 0; i < count && offset < length; i++)
            {
                var type = (TelemetryValueType)buffer[offset++];
                var name = ReadShortString(buffer, ref offset);
                properties.Add(new TelemetryProperty(name, type));
            }
            return properties;
        }

        public static byte[] BuildData(uint schemaId, uint sequence, long timestampMs,
                                       IList<TelemetryProperty> schema,
                                       IList<double> numbers, IList<string> texts)
        {
            var buffer = new byte[MaxDatagram];
            var offset = WriteHeader(buffer, TelemetryMessageType.Data);
            WriteUInt32(buffer, ref offset, schemaId);
            WriteUInt32(buffer, ref offset, sequence);
            WriteInt64(buffer, ref offset, timestampMs);

            int numberIndex = 0, textIndex = 0;
            foreach (var property in schema)
            {
                if (property.Type == TelemetryValueType.Text)
                {
                    var text = textIndex < texts.Count ? texts[textIndex] : string.Empty;
                    textIndex++;
                    if (offset + 2 + Encoding.UTF8.GetByteCount(text ?? string.Empty) > MaxDatagram) break;
                    WriteString(buffer, ref offset, text);
                }
                else if (property.Type == TelemetryValueType.Bool)
                {
                    var value = numberIndex < numbers.Count ? numbers[numberIndex] : 0d;
                    numberIndex++;
                    if (offset + 1 > MaxDatagram) break;
                    buffer[offset++] = value != 0d ? (byte)1 : (byte)0;
                }
                else
                {
                    var value = numberIndex < numbers.Count ? numbers[numberIndex] : 0d;
                    numberIndex++;
                    if (offset + 8 > MaxDatagram) break;
                    WriteDouble(buffer, ref offset, value);
                }
            }

            var datagram = new byte[offset];
            Buffer.BlockCopy(buffer, 0, datagram, 0, offset);
            return datagram;
        }

        /// <summary>Decodes a data frame against a known schema. Returns null if the frame is short.</summary>
        public static TelemetrySample ReadData(byte[] buffer, int length, IList<TelemetryProperty> schema)
        {
            var offset = HeaderLength;
            if (length < offset + 16) return null;

            var sample = new TelemetrySample
            {
                SchemaId = ReadUInt32(buffer, ref offset),
                Sequence = ReadUInt32(buffer, ref offset),
                TimestampMs = ReadInt64(buffer, ref offset)
            };

            var numbers = new List<double>(schema.Count);
            var texts = new List<string>();

            foreach (var property in schema)
            {
                if (property.Type == TelemetryValueType.Text)
                {
                    if (offset + 2 > length) break;
                    texts.Add(ReadString(buffer, ref offset));
                }
                else if (property.Type == TelemetryValueType.Bool)
                {
                    if (offset + 1 > length) break;
                    numbers.Add(buffer[offset++] != 0 ? 1d : 0d);
                }
                else
                {
                    if (offset + 8 > length) break;
                    numbers.Add(ReadDouble(buffer, ref offset));
                }
            }

            sample.Numbers = numbers.ToArray();
            sample.Texts = texts.ToArray();
            return sample;
        }

        public static byte[] BuildSubscribe(IList<string> names)
        {
            var buffer = new byte[MaxDatagram];
            var offset = WriteHeader(buffer, TelemetryMessageType.Subscribe);

            // Count is patched after writing, so an oversized list is truncated cleanly.
            var countOffset = offset;
            offset += 2;

            var written = 0;
            foreach (var name in names)
            {
                var size = 1 + Encoding.UTF8.GetByteCount(name ?? string.Empty);
                if (offset + size > MaxDatagram) break;
                WriteShortString(buffer, ref offset, name);
                written++;
            }

            var patch = countOffset;
            WriteUInt16(buffer, ref patch, (ushort)written);

            var datagram = new byte[offset];
            Buffer.BlockCopy(buffer, 0, datagram, 0, offset);
            return datagram;
        }

        public static List<string> ReadSubscribe(byte[] buffer, int length)
        {
            var offset = HeaderLength;
            int count = ReadUInt16(buffer, ref offset);

            var names = new List<string>(count);
            for (int i = 0; i < count && offset < length; i++)
                names.Add(ReadShortString(buffer, ref offset));
            return names;
        }

        public static byte[] BuildInputState(IList<string> names, IList<double> values)
        {
            var buffer = new byte[MaxDatagram];
            var offset = WriteHeader(buffer, TelemetryMessageType.InputState);

            var countOffset = offset;
            offset += 2;

            var written = 0;
            for (int i = 0; i < names.Count && i < values.Count; i++)
            {
                var size = 1 + Encoding.UTF8.GetByteCount(names[i] ?? string.Empty) + 8;
                if (offset + size > MaxDatagram) break;
                WriteShortString(buffer, ref offset, names[i]);
                WriteDouble(buffer, ref offset, values[i]);
                written++;
            }

            var patch = countOffset;
            WriteUInt16(buffer, ref patch, (ushort)written);

            var datagram = new byte[offset];
            Buffer.BlockCopy(buffer, 0, datagram, 0, offset);
            return datagram;
        }

        public static void ReadInputState(byte[] buffer, int length, IList<string> names, IList<double> values)
        {
            var offset = HeaderLength;
            int count = ReadUInt16(buffer, ref offset);

            for (int i = 0; i < count && offset < length; i++)
            {
                names.Add(ReadShortString(buffer, ref offset));
                values.Add(ReadDouble(buffer, ref offset));
            }
        }

        public static byte[] BuildHello(string pluginVersion, string gameName, bool gameRunning)
        {
            var buffer = new byte[MaxDatagram];
            var offset = WriteHeader(buffer, TelemetryMessageType.Hello);
            WriteShortString(buffer, ref offset, pluginVersion);
            WriteShortString(buffer, ref offset, gameName);
            buffer[offset++] = gameRunning ? (byte)1 : (byte)0;

            var datagram = new byte[offset];
            Buffer.BlockCopy(buffer, 0, datagram, 0, offset);
            return datagram;
        }

        public static void ReadHello(byte[] buffer, int length,
                                     out string pluginVersion, out string gameName, out bool gameRunning)
        {
            var offset = HeaderLength;
            pluginVersion = ReadShortString(buffer, ref offset);
            gameName = ReadShortString(buffer, ref offset);
            gameRunning = offset < length && buffer[offset] != 0;
        }

        public static byte[] BuildRequestCatalog()
        {
            var buffer = new byte[HeaderLength];
            WriteHeader(buffer, TelemetryMessageType.RequestCatalog);
            return buffer;
        }

        /// <summary>
        /// Stable hash of a subscribed property set, used as the schema id so the bridge can tell
        /// whether a data frame matches the schema it holds.
        /// </summary>
        public static uint ComputeSchemaId(IList<TelemetryProperty> properties)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var property in properties)
                {
                    foreach (var c in property.Name)
                    {
                        hash ^= c;
                        hash *= 16777619;
                    }
                    hash ^= (byte)property.Type;
                    hash *= 16777619;
                }
                return hash;
            }
        }
    }
}
