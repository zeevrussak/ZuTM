// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Core.Net;
using Xunit;

namespace ZuTM.Core.Tests.Net;

public class RemoteImageTests
{
    [Theory]
    [InlineData("https://releases.ubuntu.com/noble/ubuntu-24.04.3-desktop-amd64.iso")]
    [InlineData("http://mirror.local:8080/debian-13.1.0-amd64-netinst.iso")]
    [InlineData("ftp://ftp.example.org/isos/alpine-virt-3.24.2-x86_64.iso")]
    [InlineData("ftps://secure.example.org/isos/install.iso")]
    public void Accepts_CurlSupportedSchemes(string url) =>
        Assert.True(RemoteImage.IsRemoteLocation(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\isos\\ubuntu-24.04.3-desktop-amd64.iso")]
    [InlineData("file:///C:/isos/ubuntu.iso")]
    [InlineData("releases.ubuntu.com/noble/ubuntu.iso")]
    [InlineData("sftp://example.org/install.iso")]
    [InlineData("https://releases.ubuntu.com/ubuntu,a,b.iso")]
    public void Rejects_NonStreamableLocations(string? location) =>
        Assert.False(RemoteImage.IsRemoteLocation(location));

    [Fact]
    public void Rejection_ExplainsTheProblem()
    {
        Assert.Contains("https://", RemoteImage.GetValidationError("C:\\isos\\ubuntu.iso"));

        Assert.Contains("percent-encode",
            RemoteImage.GetValidationError("https://releases.ubuntu.com/ubuntu,a.iso"));
    }

    [Fact]
    public void BlockDriver_MatchesScheme_CaseInsensitively() =>
        Assert.Equal("https", RemoteImage.QemuBlockDriver("HTTPS://releases.ubuntu.com/ubuntu.iso"));
}
