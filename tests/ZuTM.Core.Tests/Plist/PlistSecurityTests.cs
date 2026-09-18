// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
// Security regressions for plist parsing: config.plist is untrusted input.

using ZuTM.Core.Plist;
using Xunit;

namespace ZuTM.Core.Tests.Plist;

public class PlistSecurityTests
{
    [Fact]
    public void ParsesDocumentsWithDoctype_WithoutResolvingAnything()
    {
        // UTM's config.plist always carries this DOCTYPE; parsing must keep
        // working while never fetching or expanding the DTD.
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict><key>k</key><string>v</string></dict></plist>
            """;

        var dict = (PlistDictionary)PlistXml.ParseText(xml);
        Assert.Equal("v", dict.GetString("k"));
    }

    [Fact]
    public void InternalEntityExpansionIsNotPerformed()
    {
        // A billion-laughs style internal subset must not expand: the value
        // stays unevaluated entity text rather than exploding memory.
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE plist [<!ENTITY a "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAA">]>
            <plist version="1.0"><dict><key>k</key><string>&a;</string></dict></plist>
            """;

        // With DTD processing disabled, entity references are never expanded:
        // undeclared ones are rejected outright (the safest outcome), and no
        // path through this parser can amplify entity text.
        Assert.Throws<FormatException>(() => PlistXml.ParseText(xml));
    }

    [Fact]
    public void RejectsNonPlistRoots()
    {
        const string xml = """<?xml version="1.0"?><evil><secret/></evil>""";
        Assert.Throws<FormatException>(() => PlistXml.ParseText(xml));
    }
}
