// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — Apache License 2.0.

using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZuTM.Core.Net;

/// <summary>Progress of one ISO download: bytes received and total size when the server declares it.</summary>
public sealed record RemoteIsoDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Percent => TotalBytes is > 0 ? BytesReceived * 100.0 / TotalBytes.Value : null;
}

/// <summary>Why <see cref="RemoteIsoDownloader.DownloadAsync"/> returned without an exception.</summary>
public enum DownloadStopReason
{
    /// <summary>The file is complete and moved to its final destination.</summary>
    Completed,

    /// <summary>Paused by request; the staged .part file and sidecar remain for resume.</summary>
    Paused,
}

/// <summary>The destination volume lacks room for the remaining bytes of a known-size transfer.</summary>
public sealed class InsufficientDiskSpaceException(string message, long neededBytes, long freeBytes)
    : IOException(message)
{
    public long NeededBytes { get; } = neededBytes;

    public long FreeBytes { get; } = freeBytes;
}

/// <summary>
/// Sidecar next to a staged <c>.part</c> file: everything a paused download
/// needs to resume safely. <see cref="ResumeTag"/> is the strong ETag or
/// Last-Modified of the first response; it travels in If-Range on resume so
/// an upstream file that changed between sessions restarts cleanly instead
/// of splicing two different files together.
/// </summary>
public sealed record DownloadSessionMeta
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("resumeTag")]
    public string? ResumeTag { get; init; }

    [JsonPropertyName("totalBytes")]
    public long? TotalBytes { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static DownloadSessionMeta? TryLoad(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<DownloadSessionMeta>(File.ReadAllText(path), SerializerOptions)
                : null;
        }
        catch (JsonException)
        {
            return null; // unreadable sidecar = no resume state, the caller starts fresh
        }
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
}

/// <summary>
/// Streams a distributor ISO from its official URL to a destination path.
/// Downloads happen at VM-creation time because QEMU's curl block driver
/// cannot feed the system emulator on Windows — libcurl's socket cannot be
/// registered with the Win32 AIO loop (a known upstream limitation; only the
/// synchronous qemu-img path works). A downloaded ISO attaches like any
/// local image and keeps working offline.
///
/// Resumable by design: progress stages into a <c>.part</c> file with a
/// <c>.part.meta</c> sidecar. Pause keeps both; cancel and failure delete
/// both, so a truncated ISO can never look finished. Resume sends Range +
/// If-Range; a server that ignores either gets a clean restart.
/// </summary>
public sealed class RemoteIsoDownloader(HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? NetHttp.Shared.Value;

    /// <summary>Test seam for the destination-volume free-space probe (bytes).</summary>
    internal Func<string, long>? FreeSpaceProbe { get; init; }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destinationPath"/>,
    /// resuming from a previous pause when the staged file and sidecar are
    /// present and belong to the same URL.
    /// </summary>
    public async Task<DownloadStopReason> DownloadAsync(
        string url,
        string destinationPath,
        IProgress<RemoteIsoDownloadProgress>? progress = null,
        PauseToken pause = default,
        CancellationToken cancellationToken = default)
    {
        if (RemoteImage.GetValidationError(url) is { } error)
        {
            throw new ArgumentException(error, nameof(url));
        }

        var uri = new Uri(url);
        if (uri.Scheme is not ("http" or "https"))
        {
            // HttpClient cannot speak ftp; only the QEMU streaming path can.
            throw new ArgumentException("Downloads require an http or https URL.", nameof(url));
        }

        if (!DownloadHostPolicy.IsAllowed(uri))
        {
            throw new ArgumentException(
                $"Refusing to download from a non-public host: {uri.Host}", nameof(url));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var partialPath = destinationPath + ".part";
        var metaPath = destinationPath + ".part.meta";
        try
        {
            var received = 0L;
            string? resumeTag = null;
            if (DownloadSessionMeta.TryLoad(metaPath) is { } meta && meta.Url == url)
            {
                received = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
                resumeTag = received > 0 ? meta.ResumeTag : null;
            }

            HttpResponseMessage response;
            long? total;
            string? etag;
            var resuming = false;

            // One re-issue covers the "server sent 206 with a range we did not
            // ask for" corner: restart the request without Range instead of
            // failing a download that would have worked plainly.
            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (received > 0)
                {
                    request.Headers.TryAddWithoutValidation("Range", $"bytes={received}-");
                    if (resumeTag is { } tag)
                    {
                        request.Headers.TryAddWithoutValidation("If-Range", tag);
                    }
                }

                response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                etag = response.Headers.ETag?.Tag
                    ?? response.Content.Headers.LastModified?.ToString("R");
                if (received > 0 && response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range?.From == received)
                    {
                        resuming = true;
                        total = range.Length is > 0 ? range.Length : null;
                        break;
                    }

                    // Range confusion: drop the resume state and re-request plainly.
                    received = 0;
                    continue;
                }

                // Full-body response: the server ignored Range, the If-Range
                // tag mismatched (file changed), or this was never a resume.
                received = 0;
                total = response.Content.Headers.ContentLength;
                break;
            }

            EnsureDiskSpace(destinationPath, total, received);
            new DownloadSessionMeta { Url = url, ResumeTag = etag, TotalBytes = total }.Save(metaPath);

            await using (var file = new FileStream(
                partialPath,
                resuming ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);

                var buffer = new byte[1024 * 1024];
                while (true)
                {
                    // Pause leaves the loop (and disposes the response) with the
                    // staged file and sidecar intact — the caller resumes later
                    // by calling this method again.
                    if (pause.IsPaused)
                    {
                        return DownloadStopReason.Paused;
                    }

                    var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    progress?.Report(new RemoteIsoDownloadProgress(received, total));
                }

                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // The staged file must be closed before it can take its final name.
            File.Move(partialPath, destinationPath, overwrite: true);
            File.Delete(metaPath);
            return DownloadStopReason.Completed;
        }
        catch
        {
            // A cancelled or failed download must never leave a truncated ISO
            // that looks finished — only a deliberate pause may keep state.
            TryDelete(partialPath);
            TryDelete(metaPath);
            throw;
        }
    }

    private void EnsureDiskSpace(string destinationPath, long? totalBytes, long alreadyReceived)
    {
        if (totalBytes is not { } total)
        {
            return; // server did not declare a size — nothing to pre-flight against
        }

        var root = Path.GetPathRoot(Path.GetFullPath(destinationPath));
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        var free = FreeSpaceProbe?.Invoke(root) ?? new DriveInfo(root).AvailableFreeSpace;
        var needed = total - alreadyReceived;
        const long headroom = 64 * 1024 * 1024; // logs, efi vars, filesystem slack
        if (free < needed + headroom)
        {
            throw new InsufficientDiskSpaceException(
                $"Not enough space on {root}: the download needs {needed / (1024.0 * 1024):F0} MiB, "
                + $"only {free / (1024.0 * 1024):F0} MiB are free. Choose another downloads folder in Settings.",
                needed,
                free);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort — never mask the original failure.
        }
    }
}
