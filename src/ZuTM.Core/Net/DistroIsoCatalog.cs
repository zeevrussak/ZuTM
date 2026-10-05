// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Net.Http;
using System.Text.RegularExpressions;

namespace ZuTM.Core.Net;

/// <summary>An official distribution ISO that ZuTM can stream from the distributor's own servers.</summary>
public sealed record DistroIsoOffering
{
    public required string Id { get; init; }

    /// <summary>Short label shown in the catalog picker, e.g. "Ubuntu 24.04 LTS — Desktop".</summary>
    public required string DisplayName { get; init; }

    /// <summary>One-line explanation for the picker tooltip.</summary>
    public required string Description { get; init; }

    /// <summary>Guest architecture the image boots on: "x86_64" or "aarch64".</summary>
    public required string GuestArchitecture { get; init; }

    /// <summary>Distributor directory index scanned for the current ISO; null when <see cref="DirectUrl"/> is set.</summary>
    public string? IndexUrl { get; init; }

    /// <summary>Matched (case-insensitively) against ISO file names listed on the index.</summary>
    public string FileNamePattern { get; init; } = "";

    /// <summary>Distributor-published URL that always resolves to the current release; no index scan needed.</summary>
    public string? DirectUrl { get; init; }
}

/// <summary>
/// Curated official ISO offerings. Index-based entries resolve at creation
/// time (distributors keep several point releases online and purge old ones,
/// so hard-coding file names would rot); direct entries use distributor
/// "always current" URLs. All URLs are the distributors' own.
/// </summary>
public static class DistroIsoCatalog
{
    /// <summary>Offerings that boot on the given guest architecture ("x86_64" or "aarch64").</summary>
    public static IReadOnlyList<DistroIsoOffering> ForArchitecture(string guestArchitecture) =>
        All.Where(o => o.GuestArchitecture == guestArchitecture).ToArray();

    public static IReadOnlyList<DistroIsoOffering> All { get; } =
    [
        new()
        {
            Id = "ubuntu-2604-desktop-amd64",
            DisplayName = "Ubuntu 26.04 LTS — Desktop",
            Description = "Streams the newest 26.04 point release from releases.ubuntu.com.",
            GuestArchitecture = "x86_64",
            IndexUrl = "https://releases.ubuntu.com/resolute/",
            FileNamePattern = @"^ubuntu-[0-9.]+-desktop-amd64\.iso$",
        },
        new()
        {
            Id = "ubuntu-2604-server-amd64",
            DisplayName = "Ubuntu 26.04 LTS — Server",
            Description = "Streams the newest 26.04 point release from releases.ubuntu.com.",
            GuestArchitecture = "x86_64",
            IndexUrl = "https://releases.ubuntu.com/resolute/",
            FileNamePattern = @"^ubuntu-[0-9.]+-live-server-amd64\.iso$",
        },
        new()
        {
            Id = "ubuntu-2404-desktop-amd64",
            DisplayName = "Ubuntu 24.04 LTS — Desktop",
            Description = "Streams the newest 24.04 point release from releases.ubuntu.com.",
            GuestArchitecture = "x86_64",
            IndexUrl = "https://releases.ubuntu.com/noble/",
            FileNamePattern = @"^ubuntu-[0-9.]+-desktop-amd64\.iso$",
        },
        new()
        {
            Id = "ubuntu-2404-server-amd64",
            DisplayName = "Ubuntu 24.04 LTS — Server",
            Description = "Streams the newest 24.04 point release from releases.ubuntu.com.",
            GuestArchitecture = "x86_64",
            IndexUrl = "https://releases.ubuntu.com/noble/",
            FileNamePattern = @"^ubuntu-[0-9.]+-live-server-amd64\.iso$",
        },
        new()
        {
            Id = "debian-netinst-amd64",
            DisplayName = "Debian (stable) — netinst",
            Description = "Streams the current stable net-installer from cdimage.debian.org.",
            GuestArchitecture = "x86_64",
            IndexUrl = "https://cdimage.debian.org/debian-cd/current/amd64/iso-cd/",
            FileNamePattern = @"^debian-[0-9.]+-amd64-netinst\.iso$",
        },
        new()
        {
            Id = "archlinux-x86_64",
            DisplayName = "Arch Linux — current",
            Description = "Streams the current release from Arch's geo mirror (always 'latest').",
            GuestArchitecture = "x86_64",
            DirectUrl = "https://geo.mirror.pkgbuild.com/iso/latest/archlinux-x86_64.iso",
        },
        new()
        {
            Id = "alpine-virt-x86_64",
            DisplayName = "Alpine Linux (stable) — virt",
            Description = "Streams the newest stable 'virt' image (minimal, VM-optimized) from dl-cdn.alpinelinux.org.",
            GuestArchitecture = "x86_64",
            IndexUrl = "https://dl-cdn.alpinelinux.org/alpine/latest-stable/releases/x86_64/",
            FileNamePattern = @"^alpine-virt-[0-9.]+-x86_64\.iso$",
        },
        new()
        {
            Id = "opensuse-tumbleweed-net-x86_64",
            DisplayName = "openSUSE Tumbleweed — network install",
            Description = "Streams the rolling 'Current' network installer from download.opensuse.org.",
            GuestArchitecture = "x86_64",
            DirectUrl = "https://download.opensuse.org/tumbleweed/iso/openSUSE-Tumbleweed-NET-x86_64-Current.iso",
        },
        new()
        {
            Id = "ubuntu-2604-server-arm64",
            DisplayName = "Ubuntu 26.04 LTS — Server",
            Description = "Streams the newest 26.04 point release from cdimage.ubuntu.com.",
            GuestArchitecture = "aarch64",
            IndexUrl = "https://cdimage.ubuntu.com/ubuntu/releases/26.04/release/",
            FileNamePattern = @"^ubuntu-[0-9.]+-live-server-arm64\.iso$",
        },
        new()
        {
            Id = "ubuntu-2404-server-arm64",
            DisplayName = "Ubuntu 24.04 LTS — Server",
            Description = "Streams the newest 24.04 point release from cdimage.ubuntu.com.",
            GuestArchitecture = "aarch64",
            IndexUrl = "https://cdimage.ubuntu.com/ubuntu/releases/24.04/release/",
            FileNamePattern = @"^ubuntu-[0-9.]+-live-server-arm64\.iso$",
        },
        new()
        {
            Id = "debian-netinst-arm64",
            DisplayName = "Debian (stable) — netinst",
            Description = "Streams the current stable net-installer from cdimage.debian.org.",
            GuestArchitecture = "aarch64",
            IndexUrl = "https://cdimage.debian.org/debian-cd/current/arm64/iso-cd/",
            FileNamePattern = @"^debian-[0-9.]+-arm64-netinst\.iso$",
        },
        new()
        {
            Id = "alpine-virt-aarch64",
            DisplayName = "Alpine Linux (stable) — virt",
            Description = "Streams the newest stable 'virt' image (minimal, VM-optimized) from dl-cdn.alpinelinux.org.",
            GuestArchitecture = "aarch64",
            IndexUrl = "https://dl-cdn.alpinelinux.org/alpine/latest-stable/releases/aarch64/",
            FileNamePattern = @"^alpine-virt-[0-9.]+-aarch64\.iso$",
        },
        new()
        {
            Id = "opensuse-tumbleweed-net-aarch64",
            DisplayName = "openSUSE Tumbleweed — network install",
            Description = "Streams the rolling 'Current' network installer from download.opensuse.org.",
            GuestArchitecture = "aarch64",
            DirectUrl = "https://download.opensuse.org/tumbleweed/iso/openSUSE-Tumbleweed-NET-aarch64-Current.iso",
        },
    ];
}

/// <summary>Shared HTTP client for distributor metadata and ISO downloads. No global timeout: metadata lookups scope their own, ISO downloads run until they finish or the user cancels.</summary>
internal static class NetHttp
{
    public static readonly Lazy<HttpClient> Shared = new(() =>
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZuTM (+https://github.com/zeevrussak/ZuTM)");
        return client;
    });
}

/// <summary>Resolves a catalog offering to the exact ISO URL to fetch right now.</summary>
public sealed class DistroIsoResolver
{
    private static readonly TimeSpan IndexTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;

    public DistroIsoResolver(HttpClient? http = null) => _http = http ?? NetHttp.Shared.Value;

    /// <summary>
    /// Returns the exact URL of the offering's current ISO. Direct offerings
    /// return their URL untouched; index offerings fetch the distributor's
    /// directory listing and pick the newest version-matching file.
    /// </summary>
    public async Task<string> ResolveUrlAsync(DistroIsoOffering offering, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(offering);
        if (offering.DirectUrl is not null)
        {
            return offering.DirectUrl;
        }

        if (offering.IndexUrl is null)
        {
            throw new InvalidOperationException($"Offering '{offering.Id}' has neither a direct URL nor an index URL.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(IndexTimeout);
        var html = await _http.GetStringAsync(offering.IndexUrl, timeout.Token).ConfigureAwait(false);
        var fileName = PickNewestIsoFileName(html, offering.FileNamePattern);
        if (fileName is null)
        {
            throw new InvalidOperationException(
                $"No matching ISO is currently listed at {offering.IndexUrl} for {offering.DisplayName}. " +
                "Enter the image URL manually instead.");
        }

        return CombineUrl(offering.IndexUrl, fileName);
    }

    /// <summary>
    /// Newest (version-aware) ISO file name whose href matches the pattern,
    /// or null when the index lists none. Distributors keep several point
    /// releases listed at once — 24.04.3 stays up after 24.04.5 lands — and
    /// plain string sorting would pick 24.04.10 below 24.04.3, so numeric
    /// segments are compared as integers.
    /// </summary>
    internal static string? PickNewestIsoFileName(string indexHtml, string fileNamePattern)
    {
        var pattern = new Regex(fileNamePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var matches = HrefFileNames(indexHtml)
            .Where(name => pattern.IsMatch(name))
            .ToList();
        return matches.Count == 0
            ? null
            : matches.OrderBy(VersionTokens, TokenSequenceComparer.Instance).First();
    }

    private static IEnumerable<string> HrefFileNames(string indexHtml)
    {
        foreach (Match match in Regex.Matches(indexHtml, "href\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
        {
            var href = match.Groups[1].Value;
            var fileName = href[(href.LastIndexOf('/') + 1)..];
            var query = fileName.IndexOfAny(['?', '#']);
            if (query >= 0)
            {
                fileName = fileName[..query];
            }

            if (fileName.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            {
                yield return fileName;
            }
        }
    }

    private static long[] VersionTokens(string fileName) =>
        [.. Regex.Matches(fileName, "[0-9]+").Select(m => long.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture))];

    /// <summary>Descending numeric-sequence comparison; a missing segment compares as 0 (24.04.5 &lt; 24.04.5.1).</summary>
    private sealed class TokenSequenceComparer : IComparer<long[]>
    {
        public static readonly TokenSequenceComparer Instance = new();

        public int Compare(long[]? x, long[]? y)
        {
            var length = Math.Max(x?.Length ?? 0, y?.Length ?? 0);
            for (var i = 0; i < length; i++)
            {
                var left = i < (x?.Length ?? 0) ? x![i] : 0;
                var right = i < (y?.Length ?? 0) ? y![i] : 0;
                if (left != right)
                {
                    return right.CompareTo(left); // descending: newest first
                }
            }

            return 0;
        }
    }

    private static string CombineUrl(string indexUrl, string fileName) =>
        fileName.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : indexUrl.TrimEnd('/') + "/" + fileName;
}
