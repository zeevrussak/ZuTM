// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Core.Plist;
using Xunit;

namespace ZuTM.Core.Tests.Plist;

public class PlistDocumentTests
{
    [Fact]
    public void Sniffs_XmlAndBinary()
    {
        var dict = PlistFactory.Dict(("key", new PlistString("value")));

        var fromXml = (PlistDictionary)PlistDocument.Parse(PlistDocument.Write(dict, PlistFormat.Xml));
        var fromBinary = (PlistDictionary)PlistDocument.Parse(PlistDocument.Write(dict, PlistFormat.Binary));

        Assert.Equal("value", fromXml.GetString("key"));
        Assert.Equal("value", fromBinary.GetString("key"));
    }

    [Fact]
    public void ParseDictionary_ThrowsForNonDictRoot()
    {
        Assert.Throws<FormatException>(() => PlistDocument.ParseDictionary(PlistXml.Write(new PlistString("x"))));
    }
}
