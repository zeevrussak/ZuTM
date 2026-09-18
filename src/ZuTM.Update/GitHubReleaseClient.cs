// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ZuTM.Update;

public sealed record ReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; init; } = "";

    [JsonPropertyName("size")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("digest")]
    public string? Digest { get; init; } // GitHub sha256:... asset digest (2025+ API)
}

public sealed record ReleaseInfo
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; init; } = "";

    [JsonPropertyName("name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("body")]
    public string? Notes { get; init; }

    [JsonPropertyName("prerelease")]
    public bool IsPreRelease { get; init; }

    [JsonPropertyName("draft")]
    public bool IsDraft { get; init; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; init; }

    [JsonPropertyName("assets")]
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];

    /// <summary>Resolves the asset matching the running process architecture (ZuTM-x64.msi / ZuTM-arm64.msi).</summary>
    public ReleaseAsset? FindInstallerAsset(string architectureName) =>
        Assets.FirstOrDefault(a => a.Name.Equals($"ZuTM-{architectureName}.msi", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Result of an update check.</summary>
public sealed record UpdateCheckResult
{
    public required SemanticVersion CurrentVersion { get; init; }

    public required SemanticVersion LatestVersion { get; init; }

    public bool UpdateAvailable { get; init; }

    public ReleaseAsset? Asset { get; init; }

    public string? Notes { get; init; }
}

/// <summary>Retrieves release metadata from GitHub (api.github.com) behind an interface for testability.</summary>
public interface IReleaseFeed
{
    Task<ReleaseInfo?> GetLatestStableAsync(CancellationToken cancellationToken = default);
}

public sealed class GitHubReleaseFeed(HttpClient httpClient, string repository) : IReleaseFeed
{
    private const string UserAgent = "ZuTM-Updater/1.0 (+" + "https://github.com/zutm/ZuTM)";

    public GitHubReleaseFeed(string repository)
        : this(CreateHttpClient(), repository)
    {
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { UseProxy = HttpClient.DefaultProxy != null });
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public async Task<ReleaseInfo?> GetLatestStableAsync(CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var owner = repository.Split('/')[0];
        var name = repository.Contains('/') ? repository.Split('/')[1] : throw new ArgumentException("Repository must be 'owner/name'.");

        var response = await httpClient.GetAsync(
            $"https://api.github.com/repos/{owner}/{name}/releases/latest",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null; // offline, rate-limited, or repo not yet public
        }

        return await response.Content.ReadFromJsonAsync<ReleaseInfo>(cancellationToken);
    }
}

/// <summary>Orchestrates the update check: compare versions, pick the right asset.</summary>
public sealed class UpdateChecker(IReleaseFeed feed)
{
    private static string ArchitectureName => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        _ => "x64",
    };

    public static string CurrentArchitectureName => ArchitectureName;

    public async Task<UpdateCheckResult?> CheckAsync(SemanticVersion currentVersion, CancellationToken cancellationToken = default)
    {
        var latest = await feed.GetLatestStableAsync(cancellationToken);
        if (latest?.TagName is not { Length: > 0 })
        {
            return null;
        }

        if (!SemanticVersion.TryParse(latest.TagName, out var latestVersion))
        {
            return null;
        }

        var asset = latest.FindInstallerAsset(ArchitectureName);
        return new UpdateCheckResult
        {
            CurrentVersion = currentVersion,
            LatestVersion = latestVersion,
            UpdateAvailable = latestVersion.IsNewerThan(currentVersion) && asset is not null,
            Asset = asset,
            Notes = latest.Notes,
        };
    }
}
