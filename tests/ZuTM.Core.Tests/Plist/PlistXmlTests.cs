// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Core.Plist;
using Xunit;

namespace ZuTM.Core.Tests.Plist;

public class PlistXmlTests
{
    [Fact]
    public void RoundTrips_AllScalarTypes()
    {
        var dict = new PlistDictionary
        {
            ["string"] = new PlistString("hello world"),
            ["unicode"] = new PlistString("דיסק — 中文 🚀"),
            ["integer"] = new PlistInteger(-42),
            ["bigInteger"] = new PlistInteger(long.MaxValue),
            ["real"] = new PlistReal(3.14159),
            ["true"] = new PlistBoolean(true),
            ["false"] = new PlistBoolean(false),
            ["data"] = new PlistData([1, 2, 3, 0, 255]),
        };

        var parsed = PlistXml.Parse(PlistXml.Write(dict)) as PlistDictionary;

        Assert.NotNull(parsed);
        Assert.Equal("hello world", parsed.GetString("string"));
        Assert.Equal("דיסק — 中文 🚀", parsed.GetString("unicode"));
        Assert.Equal(-42, parsed.GetInteger("integer", 0));
        Assert.Equal(long.MaxValue, parsed.GetInteger("bigInteger", 0));
        Assert.Equal(3.14159, parsed.GetReal("real", 0));
        Assert.True(parsed.GetBoolean("true", false));
        Assert.False(parsed.GetBoolean("false", true));
        Assert.Equal([1, 2, 3, 0, 255], parsed.GetData("data")!.Value);
    }

    [Fact]
    public void RoundTrips_NestedStructures_PreservingKeyOrder()
    {
        var nested = PlistFactory.Dict(
            ("outer", PlistFactory.Dict(
                ("alpha", new PlistInteger(1)),
                ("beta", PlistFactory.Array(new PlistString("x"), PlistFactory.Dict(("deep", new PlistBoolean(true))))))),
            ("tail", new PlistString("end")));

        var parsed = (PlistDictionary)PlistXml.Parse(PlistXml.Write(nested));

        Assert.Equal(["outer", "tail"], parsed.Keys.ToArray());
        var outer = parsed.GetDictionary("outer")!;
        Assert.Equal(["alpha", "beta"], outer.Keys.ToArray());
        Assert.Equal("x", ((PlistString)outer.GetArray("beta")![0]).Value);
        var deepDict = (PlistDictionary)outer.GetArray("beta")![1];
        Assert.True(((PlistBoolean)deepDict["deep"]).Value);
    }

    [Fact]
    public void RoundTrips_Dates_InUtc()
    {
        var date = new PlistDate(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var parsed = (PlistDate)((PlistDictionary)PlistXml.Parse(PlistXml.Write(PlistFactory.Dict(("when", date)))))["when"]!;

        Assert.Equal(date.Value, parsed.Value);
    }

    [Fact]
    public void Writes_AppleXmlShape()
    {
        var bytes = PlistXml.Write(new PlistString("v"));
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.StartsWith("<?xml", text);
        Assert.Contains("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\"", text);
        Assert.Contains("<plist version=\"1.0\">", text);
        Assert.EndsWith("</plist>", text);
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("<plist><dict><key>a</dict></dict></plist>")]
    [InlineData("<plist><dict><key>only</key></dict></plist>")]
    [InlineData("<plist><frobnicator/></plist>")]
    [InlineData("<root><not-plist/></root>")]
    public void Rejects_MalformedDocuments(string xml)
    {
        Assert.Throws<FormatException>(() => PlistXml.ParseText(xml));
    }

    [Fact]
    public void Rejects_IntegerOverflow()
    {
        Assert.Throws<FormatException>(
            () => PlistXml.ParseText("<plist><integer>not-a-number</integer></plist>"));
    }

    [Fact]
    public void Parses_UtmShapedDocument()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Backend</key>
                <string>QEMU</string>
                <key>ConfigurationVersion</key>
                <integer>4</integer>
                <key>Flags</key>
                <array>
                    <string>+sse4.2</string>
                    <string>-aes</string>
                </array>
            </dict>
            </plist>
            """;

        var dict = (PlistDictionary)PlistXml.ParseText(xml);

        Assert.Equal("QEMU", dict.GetString("Backend"));
        Assert.Equal(4, dict.GetInteger("ConfigurationVersion", 0));
        Assert.Equal("+sse4.2", ((PlistString)dict.GetArray("Flags")![0]).Value);
        Assert.Equal("-aes", ((PlistString)dict.GetArray("Flags")![1]).Value);
    }
}
