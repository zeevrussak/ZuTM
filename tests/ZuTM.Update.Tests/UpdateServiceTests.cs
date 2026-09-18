// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Net;
using System.Security.Cryptography;
using Xunit;

namespace ZuTM.Update.Tests;

/// <summary>Serve canned JSON/bytes without a network.</summary>
internal sealed class MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return await handler(request);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    public static MockHttpMessageHandler WithReleaseJson(string json) => new(_ => Task.FromResult(Json(json)));

    public static MockHttpMessageHandler NotFound() => new(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
}

public class UpdateCheckerTests
{
    private const string ReleaseJson = """
        {
          "tag_name": "v0.2.0",
          "name": "ZuTM 0.2.0",
          "body": "Bug fixes and ARM64 sparkle.",
          "prerelease": false,
          "draft": false,
          "published_at": "2026-09-01T00:00:00Z",
          "assets": [
            { "name": "ZuTM-x64.msi", "browser_download_url": "https://example/ZuTM-x64.msi", "size": 1000, "digest": "sha256:aa" },
            { "name": "ZuTM-arm64.msi", "browser_download_url": "https://example/ZuTM-arm64.msi", "size": 900 }
          ]
        }
        """;

    [Fact]
    public async Task Detects_NewerRelease_WithAssetForArchitecture()
    {
        var feed = new GitHubReleaseFeed(
            new HttpClient(MockHttpMessageHandler.WithReleaseJson(ReleaseJson)),
            "zeevrussak/ZuTM");
        var checker = new UpdateChecker(feed);

        var result = await checker.CheckAsync(SemanticVersion.Parse("0.1.0"));

        Assert.NotNull(result);
        Assert.True(result!.UpdateAvailable);
        Assert.Equal("0.2.0", result.LatestVersion.ToString());
        Assert.Equal($"ZuTM-{UpdateChecker.CurrentArchitectureName}.msi", result.Asset!.Name);
        Assert.Contains("ARM64 sparkle", result.Notes);
    }

    [Fact]
    public async Task NoUpdate_WhenCurrentIsLatest()
    {
        var feed = new GitHubReleaseFeed(
            new HttpClient(MockHttpMessageHandler.WithReleaseJson(ReleaseJson)),
            "zeevrussak/ZuTM");

        var result = await new UpdateChecker(feed).CheckAsync(SemanticVersion.Parse("0.2.0"));

        Assert.NotNull(result);
        Assert.False(result!.UpdateAvailable);
    }

    [Fact]
    public async Task ReturnsNull_WhenFeedUnreachable()
    {
        var feed = new GitHubReleaseFeed(
            new HttpClient(MockHttpMessageHandler.NotFound()),
            "zeevrussak/ZuTM");

        var result = await new UpdateChecker(feed).CheckAsync(SemanticVersion.Parse("0.1.0"));

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_WhenTagMalformed()
    {
        var json = """{ "tag_name": "not-a-version", "assets": [] }""";
        var feed = new GitHubReleaseFeed(
            new HttpClient(MockHttpMessageHandler.WithReleaseJson(json)),
            "zeevrussak/ZuTM");

        var result = await new UpdateChecker(feed).CheckAsync(SemanticVersion.Parse("0.1.0"));

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateUnavailable_WhenArchitectureAssetMissing()
    {
        var json = """
            { "tag_name": "v9.0.0", "assets": [ { "name": "ZuTM-x64.msi", "browser_download_url": "https://example/x" } ] }
            """;
        var feed = new GitHubReleaseFeed(
            new HttpClient(MockHttpMessageHandler.WithReleaseJson(json)),
            "zeevrussak/ZuTM");

        var result = await new UpdateChecker(feed).CheckAsync(SemanticVersion.Parse("0.1.0"));

        Assert.NotNull(result);
        // On ARM64 hosts the x64-only release cannot self-update; on x64 it can.
        var expected = UpdateChecker.CurrentArchitectureName == "x64";
        Assert.Equal(expected, result!.UpdateAvailable);
    }
}

public class UpdateInstallerTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("zutm-update-tests-").FullName;

    private static (UpdateInstaller Installer, MockHttpMessageHandler Handler) Create(byte[] payload)
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        }));
        return (new UpdateInstaller(new HttpClient(handler)), handler);
    }

    private string PayloadPath(string name, byte[] payload)
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllBytes(path, payload);
        return path;
    }

    [Fact]
    public async Task DownloadVerified_WritesFile_AndAcceptsCorrectHash()
    {
        var payload = "msi-bytes"u8.ToArray();
        var (installer, _) = Create(payload);
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
        var asset = new ReleaseAsset { Name = "ZuTM-x64.msi", DownloadUrl = "https://example/ZuTM-x64.msi", SizeBytes = payload.Length };

        var downloaded = await installer.DownloadVerifiedAsync(asset, $"sha256:{sha}");

        Assert.True(File.Exists(downloaded));
        Assert.Equal(payload, await File.ReadAllBytesAsync(downloaded));
    }

    [Fact]
    public async Task DownloadVerified_Rejects_TamperedPayload()
    {
        var payload = "msi-bytes"u8.ToArray();
        var (installer, _) = Create(payload);
        var asset = new ReleaseAsset { Name = "ZuTM-x64.msi", DownloadUrl = "https://example/ZuTM-x64.msi", SizeBytes = payload.Length };

        await Assert.ThrowsAsync<UpdateIntegrityException>(
            () => installer.DownloadVerifiedAsync(asset, "deadbeef" + new string('0', 56)));

        // Tampered file must not linger in %TEMP%.
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "ZuTM-update-ZuTM-x64.msi")));
    }

    [Fact]
    public async Task DownloadVerified_Rejects_SizeMismatch()
    {
        var payload = "short"u8.ToArray();
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
        var (installer, _) = Create(payload);
        var asset = new ReleaseAsset { Name = "ZuTM-x64.msi", DownloadUrl = "https://example/ZuTM-x64.msi", SizeBytes = 999_999 };

        await Assert.ThrowsAsync<UpdateIntegrityException>(() => installer.DownloadVerifiedAsync(asset, sha));
    }

    [Fact]
    public async Task DownloadVerified_ReportsProgress()
    {
        var payload = new byte[300_000];
        Random.Shared.NextBytes(payload);
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
        var (installer, _) = Create(payload);
        var asset = new ReleaseAsset { Name = "ZuTM-x64.msi", DownloadUrl = "https://example/ZuTM-x64.msi", SizeBytes = payload.Length };

        var lastProgress = 0L;
        var progress = new Progress<long>(value => lastProgress = value);

        var downloaded = await installer.DownloadVerifiedAsync(asset, sha, progress);

        Assert.Equal(payload.Length, new FileInfo(downloaded).Length);
        Assert.Equal(payload.Length, Volatile.Read(ref lastProgress));
    }

    [Fact]
    public void StartInstaller_RejectsNonMsi()
    {
        var (installer, _) = Create("x"u8.ToArray());
        var notMsi = PayloadPath("evil.exe", "x"u8.ToArray());

        Assert.Throws<ArgumentException>(() => UpdateInstaller.StartInstaller(notMsi));
    }

    [Fact]
    public void StartInstaller_RejectsMissingFile()
    {
        var (installer, _) = Create("x"u8.ToArray());
        Assert.Throws<ArgumentException>(() => UpdateInstaller.StartInstaller(Path.Combine(_tempRoot, "ghost.msi")));
    }


    [Fact]
    public async Task DownloadVerified_SanitizesHostileAssetNames()
    {
        // A hostile feed names its asset "..\..\evil.exe": the download
        // must stay inside %TEMP% under a sanitized name.
        var payload = "msi-bytes"u8.ToArray();
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
        var (installer, _) = Create(payload);
        var asset = new ReleaseAsset
        {
            Name = "..\\..\\evil.exe",
            DownloadUrl = "https://example/ZuTM-x64.msi",
            SizeBytes = payload.Length,
        };

        var downloaded = await installer.DownloadVerifiedAsync(asset, sha);

        var fileName = Path.GetFileName(downloaded);
        Assert.StartsWith("ZuTM-update-", fileName, StringComparison.Ordinal);
        Assert.DoesNotContain("..", fileName, StringComparison.Ordinal);
        Assert.True(Path.GetFullPath(downloaded).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            $"download escaped temp: {downloaded}");
        Assert.Equal(payload, await File.ReadAllBytesAsync(downloaded));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
            File.Delete(Path.Combine(Path.GetTempPath(), "ZuTM-update-ZuTM-x64.msi"));
        }
        catch (IOException)
        {
        }
    }
}
