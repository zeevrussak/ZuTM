// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Core.Plist;
using Xunit;

namespace ZuTM.Core.Tests.Plist;

public class PlistBinaryTests
{
    [Fact]
    public void RoundTrips_AllScalarTypes()
    {
        var dict = new PlistDictionary
        {
            ["ascii"] = new PlistString("plain ascii"),
            ["utf16"] = new PlistString("דיסק — 中文"),
            ["tiny-int"] = new PlistInteger(7),
            ["two-byte"] = new PlistInteger(1000),
            ["four-byte"] = new PlistInteger(100_000),
            ["negative"] = new PlistInteger(-12_345),
            ["huge"] = new PlistInteger(long.MinValue),
            ["real"] = new PlistReal(2.5),
            ["yes"] = new PlistBoolean(true),
            ["no"] = new PlistBoolean(false),
            ["data"] = new PlistData([0, 1, 2, 3, 250, 251, 252]),
        };

        var parsed = (PlistDictionary)PlistBinary.Parse(PlistBinary.Write(dict));

        Assert.Equal("plain ascii", parsed.GetString("ascii"));
        Assert.Equal("דיסק — 中文", parsed.GetString("utf16"));
        Assert.Equal(7, parsed.GetInteger("tiny-int", -1));
        Assert.Equal(1000, parsed.GetInteger("two-byte", -1));
        Assert.Equal(100_000, parsed.GetInteger("four-byte", -1));
        Assert.Equal(-12_345, parsed.GetInteger("negative", -1));
        Assert.Equal(long.MinValue, parsed.GetInteger("huge", -1));
        Assert.Equal(2.5, parsed.GetReal("real", -1));
        Assert.True(parsed.GetBoolean("yes", false));
        Assert.False(parsed.GetBoolean("no", true));
        Assert.Equal([0, 1, 2, 3, 250, 251, 252], parsed.GetData("data")!.Value);
    }

    [Fact]
    public void RoundTrips_Dates_WithAppleEpoch()
    {
        var date = new PlistDate(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        var parsed = (PlistDate)PlistBinary.Parse(PlistBinary.Write(date));

        Assert.Equal(date.Value, parsed.Value);
    }

    [Fact]
    public void RoundTrips_NestedStructures()
    {
        var root = PlistFactory.Dict(
            ("list", PlistFactory.Array(
                new PlistInteger(1),
                PlistFactory.Strings("a", "b"),
                PlistFactory.Dict(("inner", new PlistString("value"))))),
            ("empty-dict", new PlistDictionary()),
            ("empty-array", new PlistArray()));

        var parsed = (PlistDictionary)PlistBinary.Parse(PlistBinary.Write(root));
        var list = parsed.GetArray("list")!;

        Assert.Equal(3, list.Count);
        var strings = (PlistArray)list[1];
        Assert.Equal("a", ((PlistString)strings[0]).Value);
        var inner = (PlistDictionary)list[2];
        Assert.Equal("value", inner.GetString("inner"));
        Assert.Empty(parsed.GetDictionary("empty-dict")!);
        Assert.Empty(parsed.GetArray("empty-array")!);
    }

    [Fact]
    public void Uses_ExtendedLength_ForLongStrings()
    {
        // 0xF length path: strings longer than 14 characters.
        var longString = new string('Z', 300);
        var dict = PlistFactory.Dict(("long", new PlistString(longString)));

        var bytes = PlistBinary.Write(dict);
        Assert.Contains(bytes, b => b == 0x5F); // ascii string with extended length marker

        var parsed = (PlistDictionary)PlistBinary.Parse(bytes);
        Assert.Equal(longString, parsed.GetString("long"));
    }

    [Fact]
    public void Uses_Utf16Marker_ForNonAsciiStrings()
    {
        var bytes = PlistBinary.Write(PlistFactory.Dict(("s", new PlistString("π"))));
        Assert.Contains(bytes, b => (b & 0xF0) == 0x60); // UTF-16BE string marker
    }

    /// <summary>
    /// Binary fixture generated independently (hand-assembled) for a dict
    /// {"A": 1}. Guards against writer/reader self-consistency masking format bugs.
    /// </summary>
    [Fact]
    public void Parses_HandAssembledFixture()
    {
        // Layout:
        //   0: "bplist00"
        //   8: dict marker 0xD1 (1 entry) + key ref 0x01 + value ref 0x02
        //  11: int 0x10 0x01 (integer 1)
        //  13: ascii string "A": 0x51 0x41
        //  15: offset table [8, 13, 11] (1-byte offsets)
        //  18: trailer
        byte[] fixture =
        [
            0x62, 0x70, 0x6C, 0x69, 0x73, 0x74, 0x30, 0x30, // bplist00
            0xD1, 0x01, 0x02,                               // dict{1}: key[1], value[2]
            0x10, 0x01,                                     // integer 1
            0x51, 0x41,                                     // string "A"
            0x08, 0x0D, 0x0B,                               // offset table
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,             // unused
            0x01,                                           // offset int size
            0x01,                                           // object ref size
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, // num objects
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // top object
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0F, // offset table offset
        ];

        var parsed = (PlistDictionary)PlistBinary.Parse(fixture);

        Assert.Equal(1, parsed.GetInteger("A", -1));
    }

    [Fact]
    public void Interop_XmlWrittenByBinaryReader_BinaryWrittenByXmlReader()
    {
        var dict = PlistFactory.Dict(("k", PlistFactory.Array(new PlistInteger(9), new PlistString("v"))));

        var viaBinary = PlistDocument.Parse(PlistDocument.Write(dict, PlistFormat.Binary));
        var viaXml = PlistDocument.Parse(PlistDocument.Write(dict, PlistFormat.Xml));

        Assert.Equal(PlistFactory.ToPrettyString(dict), PlistFactory.ToPrettyString(viaBinary));
        Assert.Equal(PlistFactory.ToPrettyString(dict), PlistFactory.ToPrettyString(viaXml));
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 0x62, 0x70, 0x6C, 0x69, 0x73, 0x74, 0x30, 0x39 })] // bplist09
    public void Rejects_InvalidDocuments(byte[] bytes)
    {
        Assert.Throws<FormatException>(() => PlistBinary.Parse(bytes));
    }
}
