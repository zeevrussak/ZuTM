// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using ZuTM.Core.Net;
using Xunit;

namespace ZuTM.Core.Tests.Net;

public class RemoteIsoDownloaderTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("zutm-downloader-tests-").FullName;

    private string Destination(string name = "install.iso") => Path.Combine(_tempRoot, name);

    private static byte[] Payload(int mebibytes)
    {
        // Deterministic pattern, not randomness: resume tests must compare
        // exact byte content and nothing here needs unpredictability.
        var payload = new byte[mebibytes * 1024 * 1024];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        return payload;
    }

    [Fact]
    public async Task DownloadAsync_WritesFile_ReportsProgress_AndCleansUpPartFile()
    {
        var payload = "FAKE-ISO-CONTENT-FOR-DOWNLOAD"u8.ToArray();
        var downloader = new RemoteIsoDownloader(StubHttp.Serve(payload));
        var reports = new ProgressCollector();

        var stop = await downloader.DownloadAsync(
            "https://releases.ubuntu.com/noble/ubuntu-24.04.3-desktop-amd64.iso", Destination(), reports);

        Assert.Equal(DownloadStopReason.Completed, stop);
        Assert.Equal(payload, File.ReadAllBytes(Destination()));
        Assert.False(File.Exists(Destination() + ".part"));
        Assert.False(File.Exists(Destination() + ".part.meta"));
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
        Assert.False(File.Exists(Destination() + ".part.meta"));
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
    public async Task Pause_KeepsPartAndSidecar_ResumeAppendsFromOffset()
    {
        var payload = Payload(3);
        var stub = new RangeStub(payload);
        var pause = new PauseTokenSource();
        var downloader = new RemoteIsoDownloader(new HttpClient(stub));

        // Pause the moment the first chunk lands — inline, on the reader thread.
        var first = await downloader.DownloadAsync(
            "https://mirror.example.org/big.iso", Destination(),
            new InlineProgress(p => pause.IsPaused = p.BytesReceived >= 1024 * 1024),
            pause: pause.Token);

        Assert.Equal(DownloadStopReason.Paused, first);
        var part = Destination() + ".part";
        Assert.False(File.Exists(Destination()));
        Assert.True(File.Exists(part));
        Assert.Equal(1024 * 1024, new FileInfo(part).Length);
        Assert.True(File.Exists(part + ".meta"));

        pause.IsPaused = false;
        var second = await downloader.DownloadAsync("https://mirror.example.org/big.iso", Destination());

        Assert.Equal(DownloadStopReason.Completed, second);
        Assert.Equal(payload, File.ReadAllBytes(Destination()));
        Assert.False(File.Exists(part));
        Assert.False(File.Exists(part + ".meta"));

        // The second request must have asked for the remainder, not the head.
        var range = Assert.Single(stub.Requests[1].Headers.Range.Ranges);
        Assert.Equal(1024 * 1024, range.From);
    }

    [Fact]
    public async Task Resume_AgainstServerThatIgnoresRanges_RestartsAndCompletes()
    {
        var payload = Payload(3);
        // No ETag, no Range support: every request gets a plain 200.
        var stub = new RangeStub(payload, etag: null, honorRanges: false);
        var pause = new PauseTokenSource();
        var downloader = new RemoteIsoDownloader(new HttpClient(stub));

        await downloader.DownloadAsync(
            "https://mirror.example.org/big.iso", Destination(),
            new InlineProgress(p => pause.IsPaused = p.BytesReceived >= 1024 * 1024),
            pause: pause.Token);
        Assert.True(File.Exists(Destination() + ".part"));

        var stop = await downloader.DownloadAsync("https://mirror.example.org/big.iso", Destination());

        Assert.Equal(DownloadStopReason.Completed, stop);
        Assert.Equal(payload, File.ReadAllBytes(Destination()));
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task Resume_WhenUpstreamFileChanged_RestartsFromZero()
    {
        var original = Payload(2);
        var updated = Payload(3);
        var stub = new RangeStub(original, etag: "\"v1\"");
        var pause = new PauseTokenSource();
        var downloader = new RemoteIsoDownloader(new HttpClient(stub));

        await downloader.DownloadAsync(
            "https://mirror.example.org/big.iso", Destination(),
            new InlineProgress(p => pause.IsPaused = p.BytesReceived >= 1024 * 1024),
            pause: pause.Token);

        // The distributor published a new ISO between sessions.
        stub.Payload = updated;
        stub.ETag = "\"v2\"";

        var stop = await downloader.DownloadAsync("https://mirror.example.org/big.iso", Destination());

        Assert.Equal(DownloadStopReason.Completed, stop);
        Assert.Equal(updated, File.ReadAllBytes(Destination()));
    }

    [Fact]
    public async Task Cancel_DeletesPartAndSidecar()
    {
        var payload = Payload(3);
        using var cancellation = new CancellationTokenSource();
        var downloader = new RemoteIsoDownloader(StubHttp.Serve(payload));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => downloader.DownloadAsync(
                "https://mirror.example.org/big.iso", Destination(),
                new InlineProgress(_ => cancellation.Cancel()),
                cancellationToken: cancellation.Token));

        Assert.False(File.Exists(Destination()));
        Assert.False(File.Exists(Destination() + ".part"));
        Assert.False(File.Exists(Destination() + ".part.meta"));
    }

    [Fact]
    public async Task PartWithoutMatchingSidecar_IsDiscardedNotResumed()
    {
        var payload = "REAL-CONTENT"u8.ToArray();
        await File.WriteAllTextAsync(Destination() + ".part", "STALE");
        // No sidecar at all: the staged bytes belong to nobody provable.

        var downloader = new RemoteIsoDownloader(StubHttp.Serve(payload));
        var stop = await downloader.DownloadAsync("https://mirror.example.org/fresh.iso", Destination());

        Assert.Equal(DownloadStopReason.Completed, stop);
        Assert.Equal(payload, File.ReadAllBytes(Destination()));
    }

    [Fact]
    public async Task CorruptSidecar_IsIgnoredNotResumed()
    {
        var payload = "REAL-CONTENT"u8.ToArray();
        await File.WriteAllTextAsync(Destination() + ".part", "STALE");
        await File.WriteAllTextAsync(Destination() + ".part.meta", "{not json");

        var downloader = new RemoteIsoDownloader(StubHttp.Serve(payload));
        var stop = await downloader.DownloadAsync("https://mirror.example.org/fresh.iso", Destination());

        Assert.Equal(DownloadStopReason.Completed, stop);
        Assert.Equal(payload, File.ReadAllBytes(Destination()));
    }

    [Fact]
    public async Task InsufficientSpace_ThrowsBeforeWritingAnything()
    {
        var payload = Payload(1);
        var downloader = new RemoteIsoDownloader(StubHttp.Serve(payload))
        {
            FreeSpaceProbe = _ => 1024,
        };

        var failure = await Assert.ThrowsAsync<InsufficientDiskSpaceException>(
            () => downloader.DownloadAsync("https://mirror.example.org/big.iso", Destination()));

        Assert.True(failure.NeededBytes > 0);
        Assert.Empty(Directory.GetFiles(_tempRoot));
    }

    [Fact]
    public async Task DownloadAsync_RefusesPrivateAndLoopbackHosts_BeforeAnyRequest()
    {
        var requests = 0;
        var downloader = new RemoteIsoDownloader(StubHttp.Respond(_ =>
        {
            Interlocked.Increment(ref requests);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        foreach (var url in new[]
                 {
                     "http://127.0.0.1/ubuntu.iso",
                     "http://localhost/ubuntu.iso",
                     "http://10.1.2.3/ubuntu.iso",
                     "http://192.168.1.5/ubuntu.iso",
                     "https://172.16.0.9/ubuntu.iso",
                     "http://[::1]/ubuntu.iso",
                     "http://169.254.1.2/ubuntu.iso",
                     "ftp://mirror.example.org/ubuntu.iso",
                 })
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => downloader.DownloadAsync(url, Destination()));
        }

        Assert.Equal(0, requests);
        Assert.Empty(Directory.GetFiles(_tempRoot));
    }

    [Fact]
    public void DownloadHostPolicy_ClassifiesAddresses()
    {
        Assert.True(DownloadHostPolicy.IsAllowed(new Uri("https://releases.ubuntu.com/x.iso")));
        Assert.True(DownloadHostPolicy.IsAllowed(new Uri("https://dl-cdn.alpinelinux.org/x.iso")));
        Assert.True(DownloadHostPolicy.IsAllowed(new Uri("https://8.8.8.8/x.iso")));

        Assert.False(DownloadHostPolicy.IsAllowed(new Uri("http://localhost/x.iso")));
        Assert.False(DownloadHostPolicy.IsAllowed(new Uri("http://foo.localhost/x.iso")));
        Assert.False(DownloadHostPolicy.IsAllowed(new Uri("http://127.0.0.1/x.iso")));
        Assert.False(DownloadHostPolicy.IsAllowed(new Uri("http://[::1]/x.iso")));
        Assert.False(DownloadHostPolicy.IsAllowed(new Uri("http://10.0.0.1/x.iso")));
        Assert.False(DownloadHostPolicy.IsAllowed(new Uri("http://172.31.255.1/x.iso")));
        Assert.False(DownloadHostPolicy.IsAllowed(new Uri("http://192.168.0.2/x.iso")));
        Assert.False(DownloadHostPolicy.IsAllowed(new Uri("http://169.254.9.9/x.iso")));
        Assert.False(DownloadHostPolicy.IsAllowed(new Uri("http://100.64.0.1/x.iso")));
        Assert.False(DownloadHostPolicy.IsAllowed(new Uri("http://224.0.0.1/x.iso")));
        Assert.False(DownloadHostPolicy.IsAllowed(new Uri("http://[fe80::1]/x.iso")));
        Assert.False(DownloadHostPolicy.IsAllowed(new Uri("http://[fd00::1]/x.iso")));
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

    /// <summary>Range-aware stub: honors Range+If-Range like a real distributor mirror.</summary>
    private sealed class RangeStub(byte[] payload, string? etag = "\"iso-etag\"", bool honorRanges = true)
        : HttpMessageHandler
    {
        public byte[] Payload { get; set; } = payload;

        public string? ETag { get; set; } = etag;

        public bool HonorRanges { get; set; } = honorRanges;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            var tagMatches = ETag is not null
                && request.Headers.TryGetValues("If-Range", out var values)
                && values.First() == ETag;
            if (HonorRanges && ETag is not null && range is { } item && tagMatches)
            {
                var from = item.From ?? 0;
                var slice = Payload[(int)from..];
                var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(slice),
                };
                partial.Content.Headers.ContentLength = slice.Length;
                partial.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(from, Payload.Length - 1, Payload.Length);
                partial.Headers.ETag = new EntityTagHeaderValue(ETag);
                return Task.FromResult(partial);
            }

            var full = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
            full.Content.Headers.ContentLength = Payload.Length;
            if (ETag is not null)
            {
                full.Headers.ETag = new EntityTagHeaderValue(ETag);
            }

            return Task.FromResult(full);
        }
    }

    private sealed class InlineProgress(Action<RemoteIsoDownloadProgress> action) : IProgress<RemoteIsoDownloadProgress>
    {
        public void Report(RemoteIsoDownloadProgress value) => action(value);
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
