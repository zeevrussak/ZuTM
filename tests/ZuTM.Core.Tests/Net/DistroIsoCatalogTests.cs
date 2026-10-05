// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using ZuTM.Core.Net;
using Xunit;

namespace ZuTM.Core.Tests.Net;

public class DistroIsoCatalogTests
{
    // Shape of the real directory listings (releases.ubuntu.com, cdimage.debian.org,
    // dl-cdn.alpinelinux.org): plain anchors to relative file names, several point
    // releases online at once.
    private const string UbuntuIndexHtml = """
        <html><head><title>Index of /noble</title></head><body>
        <h1>Ubuntu 24.04.5.1 LTS</h1>
        <table>
        <tr><td><a href="ubuntu-24.04.2-desktop-amd64.iso">ubuntu-24.04.2-desktop-amd64.iso</a></td></tr>
        <tr><td><a href="ubuntu-24.04.3-desktop-amd64.iso">ubuntu-24.04.3-desktop-amd64.iso</a></td></tr>
        <tr><td><a href="ubuntu-24.04.5.1-desktop-amd64.iso">ubuntu-24.04.5.1-desktop-amd64.iso</a></td></tr>
        <tr><td><a href="ubuntu-24.04.5-live-server-amd64.iso">ubuntu-24.04.5-live-server-amd64.iso</a></td></tr>
        <tr><td><a href="ubuntu-24.04.5.1-desktop-arm64.iso">ubuntu-24.04.5.1-desktop-arm64.iso</a></td></tr>
        <tr><td><a href="ubuntu-24.04.5.1-desktop-amd64.iso.list">ubuntu-24.04.5.1-desktop-amd64.iso.list</a></td></tr>
        <tr><td><a href="SHA256SUMS">SHA256SUMS</a></td></tr>
        </table></body></html>
        """;

    private const string DebianIndexHtml = """
        <html><body>
        <a href="debian-13.6.0-amd64-netinst.iso">debian-13.6.0-amd64-netinst.iso</a>
        <a href="debian-13.7.0-amd64-netinst.iso">debian-13.7.0-amd64-netinst.iso</a>
        <a href="debian-13.7.0-amd64-DVD-1.iso">debian-13.7.0-amd64-DVD-1.iso</a>
        <a href="../">Parent directory</a>
        </body></html>
        """;

    [Fact]
    public void PickNewest_PrefersHighestVersion_NotStringOrder()
    {
        var newest = DistroIsoResolver.PickNewestIsoFileName(
            UbuntuIndexHtml, @"^ubuntu-[0-9.]+-desktop-amd64\.iso$");

        // String sorting would pick "…24.04.2…" over "…24.04.10…"; numeric segments win.
        Assert.Equal("ubuntu-24.04.5.1-desktop-amd64.iso", newest);
    }

    [Fact]
    public void PickNewest_PadsMissingVersionSegments()
    {
        var html = """
            <a href="app-24.04.3-desktop-amd64.iso">x</a>
            <a href="app-24.04.10-desktop-amd64.iso">x</a>
            """;

        var newest = DistroIsoResolver.PickNewestIsoFileName(html, @"^app-[0-9.]+-desktop-amd64\.iso$");

        Assert.Equal("app-24.04.10-desktop-amd64.iso", newest);
    }

    [Fact]
    public void PickNewest_AppliesPatternExactly()
    {
        Assert.Equal(
            "ubuntu-24.04.5-live-server-amd64.iso",
            DistroIsoResolver.PickNewestIsoFileName(UbuntuIndexHtml, @"^ubuntu-[0-9.]+-live-server-amd64\.iso$"));

        // .iso.list siblings and foreign architectures never match.
        Assert.Null(DistroIsoResolver.PickNewestIsoFileName(UbuntuIndexHtml, @"^ubuntu-[0-9.]+-desktop-i386\.iso$"));
        Assert.Equal(
            "debian-13.7.0-amd64-netinst.iso",
            DistroIsoResolver.PickNewestIsoFileName(DebianIndexHtml, @"^debian-[0-9.]+-amd64-netinst\.iso$"));
    }

    [Fact]
    public void PickNewest_ReturnsNull_WhenNothingMatches() =>
        Assert.Null(DistroIsoResolver.PickNewestIsoFileName("<html><a href=\"SHA256SUMS\">s</a></html>", @"^.*\.iso$"));

    [Fact]
    public void ResolveUrlAsync_JoinsIndexUrl_AndNewestFileName()
    {
        var resolver = new DistroIsoResolver(StubHandler(UbuntuIndexHtml));
        var offering = DistroIsoCatalog.All.First(o => o.Id == "ubuntu-2404-desktop-amd64");

        var url = resolver.ResolveUrlAsync(offering).GetAwaiter().GetResult();

        Assert.Equal("https://releases.ubuntu.com/noble/ubuntu-24.04.5.1-desktop-amd64.iso", url);
    }

    [Fact]
    public void ResolveUrlAsync_ReturnsDirectUrl_WithoutFetching()
    {
        var requested = false;
        var resolver = new DistroIsoResolver(StubHandler(_ =>
        {
            requested = true;
            throw new InvalidOperationException("direct offerings must not fetch");
        }));
        var offering = DistroIsoCatalog.All.First(o => o.Id == "archlinux-x86_64");

        var url = resolver.ResolveUrlAsync(offering).GetAwaiter().GetResult();

        Assert.Equal("https://geo.mirror.pkgbuild.com/iso/latest/archlinux-x86_64.iso", url);
        Assert.False(requested);
    }

    [Fact]
    public async Task ResolveUrlAsync_ThrowsExplaining_WhenIndexHasNoMatch()
    {
        var resolver = new DistroIsoResolver(StubHandler("<html><a href=\"SHA256SUMS\">s</a></html>"));
        var offering = DistroIsoCatalog.All.First(o => o.Id == "debian-netinst-amd64");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveUrlAsync(offering));

        Assert.Contains(offering.IndexUrl, error.Message);
    }

    [Fact]
    public void Catalog_CoversBothGuestArchitectures_WithUniqueIds()
    {
        Assert.NotEmpty(DistroIsoCatalog.ForArchitecture("x86_64"));
        Assert.NotEmpty(DistroIsoCatalog.ForArchitecture("aarch64"));
        Assert.Equal(DistroIsoCatalog.All.Count, DistroIsoCatalog.All.Select(o => o.Id).Distinct().Count());

        foreach (var offering in DistroIsoCatalog.All)
        {
            Assert.True(
                offering.GuestArchitecture is "x86_64" or "aarch64",
                $"{offering.Id}: unknown guest architecture");
            Assert.True(
                (offering.IndexUrl is null) != (offering.DirectUrl is null),
                $"{offering.Id}: exactly one of IndexUrl/DirectUrl must be set");
            Assert.True(
                offering.IndexUrl is null || offering.IndexUrl.StartsWith("https://", StringComparison.Ordinal),
                $"{offering.Id}: index must be https");
            Assert.True(
                offering.DirectUrl is null || offering.DirectUrl.StartsWith("https://", StringComparison.Ordinal),
                $"{offering.Id}: direct URL must be https");
            try
            {
                _ = new Regex(offering.FileNamePattern);
            }
            catch (ArgumentException ex)
            {
                Assert.Fail($"{offering.Id}: invalid file name pattern: {ex.Message}");
            }
        }
    }

    private static HttpClient StubHandler(string html) =>
        StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });

    private static HttpClient StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHttpMessageHandler(responder));

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
