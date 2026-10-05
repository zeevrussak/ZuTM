// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Net.Http;

namespace ZuTM.Core.Net;

/// <summary>Progress of one ISO download: bytes received and total size when the server declares it.</summary>
public sealed record RemoteIsoDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Percent => TotalBytes is > 0 ? BytesReceived * 100.0 / TotalBytes.Value : null;
}

/// <summary>
/// Streams a distributor ISO from its official URL into the bundle.
/// Downloads happen at VM-creation time because QEMU's curl block driver
/// cannot feed the system emulator on Windows — libcurl's socket cannot be
/// registered with the Win32 AIO loop (a known upstream limitation; only the
/// synchronous qemu-img path works). A downloaded ISO attaches like any
/// local image and keeps working offline.
/// </summary>
public sealed class RemoteIsoDownloader(HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? NetHttp.Shared.Value;

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destinationPath"/>.
    /// The file is staged with a .part suffix and moved into place only when
    /// complete, so a cancelled or failed download never leaves a truncated
    /// ISO that looks finished.
    /// </summary>
    public async Task<string> DownloadAsync(
        string url,
        string destinationPath,
        IProgress<RemoteIsoDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (RemoteImage.GetValidationError(url) is { } error)
        {
            throw new ArgumentException(error, nameof(url));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var partialPath = destinationPath + ".part";
        try
        {
            var total = response.Content.Headers.ContentLength;
            await using var file = File.Create(partialPath);
            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            var buffer = new byte[1024 * 1024];
            long received = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                progress?.Report(new RemoteIsoDownloadProgress(received, total));
            }
        }
        catch
        {
            try
            {
                File.Delete(partialPath);
            }
            catch (IOException)
            {
                // Best effort — never mask the original failure.
            }

            throw;
        }

        File.Move(partialPath, destinationPath, overwrite: true);
        return destinationPath;
    }
}
