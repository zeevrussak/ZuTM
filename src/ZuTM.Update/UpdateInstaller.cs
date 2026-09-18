// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Diagnostics;
using System.Security.Cryptography;

namespace ZuTM.Update;

/// <summary>Download + integrity verification + install hand-off.</summary>
public sealed class UpdateInstaller(HttpClient httpClient)
{
    /// <summary>Downloads an asset to a temp file with progress reporting and verifies its SHA-256.</summary>
    /// <param name="asset">The release asset to download.</param>
    /// <param name="expectedSha256">Lowercase hex digest, or the GitHub "sha256:"-prefixed digest string.</param>
    /// <param name="progress">Optional byte-count progress sink.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Path of the verified installer file in %TEMP%.</returns>
    public async Task<string> DownloadVerifiedAsync(
        ReleaseAsset asset,
        string expectedSha256,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asset.DownloadUrl);
        ArgumentNullException.ThrowIfNull(expectedSha256);

        var expected = NormalizeDigest(expectedSha256);

        using var response = await httpClient.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var targetPath = Path.Combine(Path.GetTempPath(), $"ZuTM-update-{asset.Name}");
        await using (var file = File.Create(targetPath))
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
                progress?.Report(total);
            }
        }

        string actual;
        await using (var hashStream = File.OpenRead(targetPath))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(hashStream, cancellationToken));
        }
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(targetPath);
            throw new UpdateIntegrityException($"SHA-256 mismatch for {asset.Name}: expected {expected}, got {actual}.");
        }

        if (asset.SizeBytes > 0 && new FileInfo(targetPath).Length != asset.SizeBytes)
        {
            File.Delete(targetPath);
            throw new UpdateIntegrityException($"Size mismatch for {asset.Name}: expected {asset.SizeBytes} bytes.");
        }

        return targetPath;
    }

    private static string NormalizeDigest(string digest)
    {
        var value = digest.Trim();
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["sha256:".Length..];
        }

        return value.ToLowerInvariant();
    }

    /// <summary>
    /// Launches the verified MSI. Windows Installer performs a major upgrade:
    /// the old ZuTM is removed and the new one installed (see installer/ZuTM.Installer).
    /// </summary>
    public static Process StartInstaller(string installerPath, bool interactive = true)
    {
        if (!File.Exists(installerPath) || !installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Installer must be a downloaded .msi file.", nameof(installerPath));
        }

        var arguments = $"/i \"{Path.GetFullPath(installerPath)}\"";
        if (!interactive)
        {
            arguments += " /qn /norestart";
        }

        return Process.Start(new ProcessStartInfo("msiexec.exe", arguments)
        {
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to launch msiexec.");
    }
}

/// <summary>Raised when a downloaded update fails integrity verification.</summary>
public sealed class UpdateIntegrityException(string message) : Exception(message);
