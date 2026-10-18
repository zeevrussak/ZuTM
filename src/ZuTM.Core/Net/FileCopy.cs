// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — Apache License 2.0.

namespace ZuTM.Core.Net;

/// <summary>Buffered file copy with byte-level progress for long imports.</summary>
public static class FileCopy
{
    /// <summary>
    /// Copies <paramref name="sourcePath"/> to <paramref name="destinationPath"/>,
    /// reporting <see cref="RemoteIsoDownloadProgress"/> per chunk (the record
    /// shape — bytes + total — is shared with downloads on purpose). The
    /// partial destination is deleted on failure or cancellation.
    /// </summary>
    public static async Task CopyWithProgressAsync(
        string sourcePath,
        string destinationPath,
        IProgress<RemoteIsoDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source file not found.", sourcePath);
        }

        var total = new FileInfo(sourcePath).Length;
        var copied = 0L;
        try
        {
            await using var source = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, useAsync: true);
            await using var destination = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                1024 * 1024, useAsync: true);

            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                progress?.Report(new RemoteIsoDownloadProgress(copied, total));
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(destinationPath);
            throw;
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
