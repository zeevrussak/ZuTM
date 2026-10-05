// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

namespace ZuTM.Core.Net;

/// <summary>
/// Network locations for drive images that are streamed straight off a
/// distributor's server by QEMU's curl block driver — the guest reads blocks
/// on demand and nothing is stored in the bundle. A remote location lives in
/// <c>zutm-state.json</c> next to local external paths, keeping config.plist
/// pristine for UTM round-trips.
/// </summary>
public static class RemoteImage
{
    /// <summary>Schemes QEMU's curl block driver accepts (the driver name equals the scheme).</summary>
    public static readonly IReadOnlyList<string> SupportedSchemes = ["http", "https", "ftp", "ftps"];

    public static bool IsRemoteLocation(string? location) => GetValidationError(location) is null;

    /// <summary>Human-readable reason a location cannot be streamed, or null when it is valid.</summary>
    public static string? GetValidationError(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return "Enter a URL.";
        }

        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri)
            || !SupportedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            return $"URL must start with one of: {string.Join(", ", SupportedSchemes.Select(s => s + "://"))}.";
        }

        // QEMU's -drive parser splits sub-options on ',', so a literal comma
        // cannot be carried inside file.url.
        if (location.Contains(','))
        {
            return "URLs containing ',' cannot be streamed; percent-encode the comma (%2C).";
        }

        return null;
    }

    /// <summary>QEMU block driver name for a validated remote location, e.g. "https".</summary>
    public static string QemuBlockDriver(string location) =>
        new Uri(location).Scheme.ToLowerInvariant();

    /// <summary>
    /// Last path segment of a validated URL, sanitized for use as a bundle
    /// file name (the URL comes from the network, so does the file name).
    /// </summary>
    public static string SuggestedFileName(string location)
    {
        var uri = new Uri(location);
        var name = Uri.UnescapeDataString(uri.Segments.Length > 0 ? uri.Segments[^1] : "").TrimEnd('/');
        var cut = name.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            name = name[..cut];
        }

        name = string.Join("_", name.Split(Path.GetInvalidFileNameChars()))
            .Replace("..", "_", StringComparison.Ordinal);
        return name.Length > 0 ? name : "install.iso";
    }
}
