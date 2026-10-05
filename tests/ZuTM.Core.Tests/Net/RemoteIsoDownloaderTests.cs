// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Net;
using System.Net.Http;
using ZuTM.Core.Net;
using Xunit;

namespace ZuTM.Core.Tests.Net;

public class RemoteIsoDownloaderTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("zutm-downloader-tests-").FullName;

    private string Destination(string name = "install.iso") => Path.Combine(_tempRoot, name);

    [Fact]
    public async Task DownloadAsync_WritesFile_ReportsProgress_AndCleansUpPartFile()
    {
        var payload = "FAKE-ISO-CONTENT-FOR-DOWNLOAD"u8.ToArray();
        var downloader = new RemoteIsoDownloader(StubHttp.Serve(payload));
        var reports = new ProgressCollector();

        var path = await downloader.DownloadAsync(
            "https://releases.ubuntu.com/noble/ubuntu-24.04.3-desktop-amd64.iso", Destination(), reports);

        Assert.Equal(Destination(), path);
        Assert.Equal(payload, File.ReadAllBytes(path));
        Assert.False(File.Exists(Destination() + ".part"));
        var last = Assert.Single(reports.Reports);
        Assert.Equal(payload.Length, last.BytesReceived);
        Assert.Equal(payload.Length, last.TotalBytes);
        Assert.Equal(100.0, last.Percent);
    }

    [Fact]
    public async Task DownloadAsync_MidStreamFailure_DeletesPartialFile()
    {
        var chunk = new byte[1024];
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new DyingStream(chunk)) { Headers = { ContentLength = 10 * 1024 * 1024 } },
        };
        var downloader = new RemoteIsoDownloader(StubHttp.Respond(_ => response));

        await Assert.ThrowsAsync<IOException>(
            () => downloader.DownloadAsync("https://mirror.example.org/big.iso", Destination()));

        Assert.False(File.Exists(Destination()));
        Assert.False(File.Exists(Destination() + ".part"));
    }

    [Fact]
    public async Task DownloadAsync_ServerError_ThrowsAndLeavesNoFile()
    {
        var downloader = new RemoteIsoDownloader(StubHttp.Respond(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => downloader.DownloadAsync("https://releases.ubuntu.com/gone.iso", Destination()));

        Assert.False(File.Exists(Destination()));
        Assert.False(File.Exists(Destination() + ".part"));
    }

    [Fact]
    public async Task DownloadAsync_RejectsNonStreamableUrls_Upfront()
    {
        var downloader = new RemoteIsoDownloader(StubHttp.Serve([1, 2, 3]));

        await Assert.ThrowsAsync<ArgumentException>(
            () => downloader.DownloadAsync("C:\\isos\\ubuntu.iso", Destination()));
        await Assert.ThrowsAsync<ArgumentException>(
            () => downloader.DownloadAsync("https://host/ubuntu,a.iso", Destination()));

        Assert.Empty(Directory.GetFiles(_tempRoot));
    }

    [Fact]
    public void SuggestedFileName_TakesLastSegment_AndSanitizes()
    {
        Assert.Equal(
            "ubuntu-24.04.3-desktop-amd64.iso",
            RemoteImage.SuggestedFileName("https://releases.ubuntu.com/noble/ubuntu-24.04.3-desktop-amd64.iso"));
        Assert.Equal(
            "openSUSE-Tumbleweed-NET-x86_64-Current.iso",
            RemoteImage.SuggestedFileName("https://download.opensuse.org/tumbleweed/iso/openSUSE-Tumbleweed-NET-x86_64-Current.iso"));
        Assert.Equal("install.iso", RemoteImage.SuggestedFileName("https://mirror.example.org/"));
        Assert.Equal("a_b.iso", RemoteImage.SuggestedFileName("https://mirror.example.org/a:b.iso"));
    }

    private sealed class ProgressCollector : IProgress<RemoteIsoDownloadProgress>
    {
        public List<RemoteIsoDownloadProgress> Reports { get; } = [];

        public void Report(RemoteIsoDownloadProgress value) => Reports.Add(value);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup best-effort on Windows.
        }
    }
}
