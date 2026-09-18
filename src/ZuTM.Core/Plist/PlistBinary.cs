// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
// Clean-room implementation of the "bplist00" binary property list format
// (public binary plist specification, 2008). No code derived from UTM or
// Apple sources.

using System.Buffers.Binary;
using System.Text;

namespace ZuTM.Core.Plist;

/// <summary>
/// Binary property list (bplist00) reader and writer. ZuTM reads binary
/// plists defensively (UTM always writes XML, but third-party tools may
/// produce binary ones) and always writes XML itself; this writer exists
/// for completeness and round-trip testing.
/// </summary>
public static class PlistBinary
{
    private const string Magic = "bplist00";

    private const byte MarkerFalse = 0x08;
    private const byte MarkerTrue = 0x09;
    private const byte MarkerInteger = 0x10;
    private const byte MarkerReal = 0x20;
    private const byte MarkerDate = 0x30;
    private const byte MarkerData = 0x40;
    private const byte MarkerAsciiString = 0x50;
    private const byte MarkerUtf16String = 0x60;
    private const byte MarkerUid = 0x80;
    private const byte MarkerArray = 0xA0;
    private const byte MarkerDict = 0xD0;

    private const int HeaderSize = 8;   // "bplist00"
    private const int TrailerSize = 32; // 6 unused + 6 fields
    private const int MaxNestingDepth = 64;

    // -- READER -------------------------------------------------------------

    /// <summary>Parses a bplist00 document. The root may be any plist node.</summary>
    public static PlistNode Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize + TrailerSize)
        {
            throw new FormatException("Binary plist is too short.");
        }

        if (!bytes[..HeaderSize].SequenceEqual(Encoding.ASCII.GetBytes(Magic)))
        {
            throw new FormatException("Not a bplist00 binary plist.");
        }

        var trailer = bytes[^TrailerSize..];
        byte offsetIntSize = trailer[6];
        byte objectRefSize = trailer[7];
        ulong numObjects = BinaryPrimitives.ReadUInt64BigEndian(trailer[8..16]);
        ulong topObject = BinaryPrimitives.ReadUInt64BigEndian(trailer[16..24]);
        ulong offsetTableOffset = BinaryPrimitives.ReadUInt64BigEndian(trailer[24..32]);

        if (offsetIntSize is < 1 or > 8 || objectRefSize is < 1 or > 8)
        {
            throw new FormatException("Binary plist trailer declares invalid integer sizes.");
        }

        if (numObjects == 0 || numObjects > int.MaxValue || topObject >= numObjects)
        {
            throw new FormatException("Binary plist trailer declares invalid object counts.");
        }

        var offsets = new ulong[(int)numObjects];
        var offsetTable = bytes[(int)offsetTableOffset..^TrailerSize];
        for (var i = 0; i < (int)numObjects; i++)
        {
            var slot = offsetTable[(i * offsetIntSize)..];
            if (slot.Length < offsetIntSize)
            {
                throw new FormatException("Binary plist offset table is truncated.");
            }

            offsets[i] = ReadUIntBigEndian(slot[..offsetIntSize]);
        }

        var reader = new Reader(bytes.ToArray(), offsets, objectRefSize, (int)numObjects);
        return reader.ReadObject((int)topObject);
    }

    private static ulong ReadUIntBigEndian(ReadOnlySpan<byte> source) => source.Length switch
    {
        1 => source[0],
        2 => BinaryPrimitives.ReadUInt16BigEndian(source),
        4 => BinaryPrimitives.ReadUInt32BigEndian(source),
        8 => BinaryPrimitives.ReadUInt64BigEndian(source),
        _ => ReadUIntWide(source),
    };

    private static ulong ReadUIntWide(ReadOnlySpan<byte> source)
    {
        ulong value = 0;
        foreach (var b in source)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    private sealed class Reader(byte[] bytes, ulong[] offsets, byte objectRefSize, int numObjects)
    {
        private readonly byte[] _bytes = bytes;

        public PlistNode ReadObject(int index)
        {
            if ((uint)index >= (uint)offsets.Length)
            {
                throw new FormatException("Binary plist object index out of range.");
            }

            return ReadObjectAtOffset((int)offsets[index], 0);
        }

        private PlistNode ReadObjectAtOffset(int offset, int depth)
        {
            if (depth > MaxNestingDepth)
            {
                throw new FormatException("Binary plist is nested too deeply.");
            }

            if ((uint)offset >= (uint)_bytes.Length)
            {
                throw new FormatException("Binary plist object offset out of range.");
            }

            var marker = _bytes[offset];
            var kind = (byte)(marker & 0xF0);
            var sizeInfo = marker & 0x0F;

            switch (kind)
            {
                case 0x00 when marker is MarkerFalse or MarkerTrue:
                    return new PlistBoolean(marker == MarkerTrue);
                case MarkerInteger:
                    return new PlistInteger(ReadSignedInt(offset + 1, 1 << sizeInfo));
                case MarkerReal:
                    return sizeInfo switch
                    {
                        0x2 => new PlistReal(BinaryPrimitives.ReadDoubleBigEndian(Slice(offset + 1, 8))),
                        _ => throw new FormatException($"Unsupported real size {sizeInfo}."),
                    };
                case MarkerDate:
                {
                    if (sizeInfo != 0x3)
                    {
                        throw new FormatException("Unsupported date size.");
                    }

                    var seconds = BinaryPrimitives.ReadDoubleBigEndian(Slice(offset + 1, 8));
                    return new PlistDate(PlistDate.AppleEpoch.AddSeconds(seconds));
                }
                case MarkerData:
                {
                    var (length, headerLength) = ReadLength(offset, sizeInfo);
                    return new PlistData(Slice(offset + headerLength, (int)length).ToArray());
                }
                case MarkerAsciiString:
                {
                    var (length, headerLength) = ReadLength(offset, sizeInfo);
                    return new PlistString(Encoding.ASCII.GetString(Slice(offset + headerLength, (int)length)));
                }
                case MarkerUtf16String:
                {
                    var (length, headerLength) = ReadLength(offset, sizeInfo);
                    var utf16 = Slice(offset + headerLength, (int)length * 2);
                    return new PlistString(Encoding.BigEndianUnicode.GetString(utf16));
                }
                case MarkerUid:
                    return new PlistInteger(ReadSignedInt(offset + 1, sizeInfo + 1));
                case MarkerArray:
                {
                    var (count, headerLength) = ReadLength(offset, sizeInfo);
                    var array = new PlistArray();
                    for (var i = 0; i < (int)count; i++)
                    {
                        var childIndex = ReadObjectRef(offset + headerLength + (i * objectRefSize));
                        array.Add(ReadObject(childIndex));
                    }

                    return array;
                }
                case MarkerDict:
                {
                    var (count, headerLength) = ReadLength(offset, sizeInfo);
                    var dict = new PlistDictionary();
                    var refsOffset = offset + headerLength;
                    for (var i = 0; i < (int)count; i++)
                    {
                        var keyIndex = ReadObjectRef(refsOffset + (i * objectRefSize));
                        var valueIndex = ReadObjectRef(refsOffset + (((int)count + i) * objectRefSize));
                        var key = ReadObject(keyIndex) as PlistString
                            ?? throw new FormatException("Binary plist dictionary key is not a string.");
                        dict[key.Value] = ReadObject(valueIndex);
                    }

                    return dict;
                }
                default:
                    throw new FormatException($"Unknown binary plist marker 0x{marker:X2}.");
            }
        }

        private (ulong Length, int HeaderLength) ReadLength(int offset, int sizeInfo)
        {
            if (sizeInfo != 0x0F)
            {
                return ((ulong)sizeInfo, 1);
            }

            // Extended length: 0xF marker, then an int-marked byte, then big-endian length.
            var intMarker = _bytes[offset + 1];
            if ((intMarker & 0xF0) != MarkerInteger)
            {
                throw new FormatException("Invalid extended-length marker.");
            }

            var intSize = 1 << (intMarker & 0x0F);
            return ((ulong)ReadSignedInt(offset + 2, intSize), 2 + intSize);
        }

        private int ReadObjectRef(int offset)
        {
            var value = ReadUIntBigEndian(Slice(offset, objectRefSize));
            if (value >= (ulong)numObjects)
            {
                throw new FormatException("Binary plist object reference out of range.");
            }

            return (int)value;
        }

        private long ReadSignedInt(int offset, int size) => size switch
        {
            1 => (sbyte)_bytes[offset],
            2 => BinaryPrimitives.ReadInt16BigEndian(Slice(offset, 2)),
            4 => BinaryPrimitives.ReadInt32BigEndian(Slice(offset, 4)),
            8 => BinaryPrimitives.ReadInt64BigEndian(Slice(offset, 8)),
            _ => throw new FormatException($"Unsupported integer size {size}."),
        };

        private ReadOnlySpan<byte> Slice(int offset, int length)
        {
            if (offset < 0 || length < 0 || offset + (long)length > _bytes.Length)
            {
                throw new FormatException("Binary plist slice out of range.");
            }

            return _bytes.AsSpan(offset, length);
        }
    }

    // -- WRITER -------------------------------------------------------------

    /// <summary>Serializes a node as a bplist00 document.</summary>
    public static byte[] Write(PlistNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var objects = new List<PlistNode>();
        var objectIndexes = new Dictionary<PlistNode, int>();
        CollectObjects(root, objects, objectIndexes);

        int objectRefSize = SizeForCount(objects.Count);
        var objectStreams = new byte[objects.Count][];
        for (var i = 0; i < objects.Count; i++)
        {
            objectStreams[i] = EncodeObject(objects[i], objectIndexes, objectRefSize);
        }

        using var buffer = new MemoryStream();
        buffer.Write(Encoding.ASCII.GetBytes(Magic));

        var offsetsList = new int[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsetsList[i] = (int)buffer.Position;
            buffer.Write(objectStreams[i]);
        }

        int offsetTableOffset = (int)buffer.Position;
        int offsetIntSize = SizeForInteger(offsetTableOffset);
        foreach (var objectOffset in offsetsList)
        {
            WriteUIntBigEndian(buffer, (ulong)objectOffset, offsetIntSize);
        }

        var trailer = new byte[TrailerSize];
        trailer[6] = (byte)offsetIntSize;
        trailer[7] = (byte)objectRefSize;
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(8), (ulong)objects.Count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(16), 0); // top object = first collected (the root)
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(24), (ulong)offsetTableOffset);
        buffer.Write(trailer);

        return buffer.ToArray();
    }

    private static void CollectObjects(PlistNode node, List<PlistNode> objects, Dictionary<PlistNode, int> indexes)
    {
        indexes[node] = objects.Count;
        objects.Add(node);
        switch (node)
        {
            case PlistDictionary dict:
                foreach (var (key, value) in dict)
                {
                    var keyNode = new PlistString(key);
                    if (!indexes.ContainsKey(keyNode))
                    {
                        CollectObjects(keyNode, objects, indexes);
                    }

                    if (!indexes.ContainsKey(value))
                    {
                        CollectObjects(value, objects, indexes);
                    }
                }

                break;
            case PlistArray array:
                foreach (var item in array)
                {
                    if (!indexes.ContainsKey(item))
                    {
                        CollectObjects(item, objects, indexes);
                    }
                }

                break;
        }
    }

    private static byte[] EncodeObject(PlistNode node, Dictionary<PlistNode, int> indexes, int objectRefSize)
    {
        using var stream = new MemoryStream();
        switch (node)
        {
            case PlistBoolean b:
                stream.WriteByte(b.Value ? MarkerTrue : MarkerFalse);
                break;
            case PlistInteger i:
                WriteInteger(stream, i.Value);
                break;
            case PlistReal r:
            {
                stream.WriteByte((byte)(MarkerReal | 0x2));
                Span<byte> realBytes = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(realBytes, r.Value);
                stream.Write(realBytes);
                break;
            }
            case PlistDate dt:
            {
                stream.WriteByte((byte)(MarkerDate | 0x3));
                Span<byte> dateBytes = stackalloc byte[8];
                var seconds = (dt.Value - PlistDate.AppleEpoch).TotalSeconds;
                BinaryPrimitives.WriteDoubleBigEndian(dateBytes, seconds);
                stream.Write(dateBytes);
                break;
            }
            case PlistString s:
                WriteString(stream, s);
                break;
            case PlistData d:
                WriteLengthPrefixed(stream, MarkerData, (ulong)d.Value.Length);
                stream.Write(d.Value);
                break;
            case PlistArray array:
                WriteLengthPrefixed(stream, MarkerArray, (ulong)array.Count);
                foreach (var item in array)
                {
                    WriteUIntBigEndian(stream, (ulong)indexes[item], objectRefSize);
                }

                break;
            case PlistDictionary dict:
                WriteLengthPrefixed(stream, MarkerDict, (ulong)dict.Count);
                foreach (var (key, _) in dict)
                {
                    WriteUIntBigEndian(stream, (ulong)indexes[new PlistString(key)], objectRefSize);
                }

                foreach (var (_, value) in dict)
                {
                    WriteUIntBigEndian(stream, (ulong)indexes[value], objectRefSize);
                }

                break;
            default:
                throw new InvalidOperationException($"Cannot binary-encode {node.GetType()}.");
        }

        return stream.ToArray();
    }

    private static void WriteInteger(MemoryStream stream, long value)
    {
        if (value >= 0 && value <= byte.MaxValue)
        {
            stream.WriteByte((byte)(MarkerInteger | 0x0));
            stream.WriteByte((byte)value);
        }
        else if (value > byte.MaxValue && value <= ushort.MaxValue)
        {
            stream.WriteByte((byte)(MarkerInteger | 0x1));
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteInt16BigEndian(b, (short)value);
            stream.Write(b);
        }
        else if (value > ushort.MaxValue && value <= uint.MaxValue)
        {
            stream.WriteByte((byte)(MarkerInteger | 0x2));
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, (int)value);
            stream.Write(b);
        }
        else
        {
            stream.WriteByte((byte)(MarkerInteger | 0x3));
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b, value);
            stream.Write(b);
        }
    }

    private static void WriteString(MemoryStream stream, PlistString s)
    {
        var value = s.Value;
        var isAscii = value.All(c => c <= 0x7F);
        if (isAscii)
        {
            WriteLengthPrefixed(stream, MarkerAsciiString, (ulong)value.Length);
            stream.Write(Encoding.ASCII.GetBytes(value));
        }
        else
        {
            WriteLengthPrefixed(stream, MarkerUtf16String, (ulong)value.Length);
            stream.Write(Encoding.BigEndianUnicode.GetBytes(value));
        }
    }

    private static void WriteLengthPrefixed(MemoryStream stream, byte markerKind, ulong length)
    {
        if (length < 0x0F)
        {
            stream.WriteByte((byte)(markerKind | (int)length));
            return;
        }

        stream.WriteByte((byte)(markerKind | 0x0F));
        if (length <= byte.MaxValue)
        {
            stream.WriteByte((byte)(MarkerInteger | 0x0));
            stream.WriteByte((byte)length);
        }
        else if (length <= ushort.MaxValue)
        {
            stream.WriteByte((byte)(MarkerInteger | 0x1));
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)length);
            stream.Write(b);
        }
        else if (length <= uint.MaxValue)
        {
            stream.WriteByte((byte)(MarkerInteger | 0x2));
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)length);
            stream.Write(b);
        }
        else
        {
            stream.WriteByte((byte)(MarkerInteger | 0x3));
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(b, length);
            stream.Write(b);
        }
    }

    private static void WriteUIntBigEndian(MemoryStream stream, ulong value, int size)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, value);
        stream.Write(b[^size..]);
    }

    private static int SizeForCount(int count) => count switch
    {
        <= byte.MaxValue => 1,
        <= ushort.MaxValue => 2,
        <= 0xFFFFFF => 3,
        _ => 4,
    };

    private static int SizeForInteger(int maxValue) => maxValue switch
    {
        <= byte.MaxValue => 1,
        <= ushort.MaxValue => 2,
        <= 0xFFFFFF => 3,
        _ => 4,
    };
}
