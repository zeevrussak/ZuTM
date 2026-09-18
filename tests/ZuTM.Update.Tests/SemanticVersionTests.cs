// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Update;
using Xunit;

namespace ZuTM.Update.Tests;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("1.0.0", 1, 0, 0, false)]
    [InlineData("v2.3.4", 2, 3, 4, false)]
    [InlineData("0.10.0", 0, 10, 0, false)]
    [InlineData("1.2.3-rc.1", 1, 2, 3, true)]
    [InlineData("1.2.3+build.42", 1, 2, 3, false)]
    [InlineData("1.2.3-beta+meta", 1, 2, 3, true)]
    public void Parses_CorePreBuild(string text, int major, int minor, int patch, bool preRelease)
    {
        var version = SemanticVersion.Parse(text);

        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(preRelease, version.IsPreRelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    [InlineData("01.2.3")]
    public void Rejects_MalformedVersions(string text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    [Fact]
    public void BuildMetadata_IgnoredForPrecedence()
    {
        var a = SemanticVersion.Parse("1.0.0+one");
        var b = SemanticVersion.Parse("1.0.0+two");

        Assert.Equal(0, a.CompareTo(b));
    }

    [Theory]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("1.1.0", "1.0.99")]
    [InlineData("1.0.10", "1.0.9")]
    [InlineData("1.0.0", "1.0.0-rc.1")]       // release beats pre-release
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1")]  // numeric identifiers compare numerically
    [InlineData("1.0.0-rc.1", "1.0.0-alpha")] // rc > alpha lexically
    [InlineData("1.0.0-rc.1", "1.0.0-rc")]    // fewer identifiers sort lower
    public void Precedence(string newer, string older)
    {
        Assert.True(SemanticVersion.Parse(newer).IsNewerThan(SemanticVersion.Parse(older)), $"{newer} should be newer than {older}");
    }

    [Fact]
    public void RoundTrips_ToString()
    {
        var text = "1.2.3-rc.1+build.5";
        Assert.Equal(text, SemanticVersion.Parse(text).ToString());
    }
}
