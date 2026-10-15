// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Core.Net;
using Xunit;

namespace ZuTM.Core.Tests.Net;

public class FileCopyTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("zutm-filecopy-tests-").FullName;

    private string Source(string name = "source.iso") => Path.Combine(_tempRoot, name);

    private string Destination(string name = "copy.iso") => Path.Combine(_tempRoot, name);

    [Fact]
    public async Task Copy_ReportsMonotonicProgress_AndWritesExactContent()
    {
        var payload = new byte[256 * 1024];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        await File.WriteAllBytesAsync(Source(), payload);
        var reports = new List<RemoteIsoDownloadProgress>();

        await FileCopy.CopyWithProgressAsync(
            Source(), Destination(), new InlineProgress(reports.Add));

        Assert.Equal(payload, File.ReadAllBytes(Destination()));
        Assert.True(reports.Count > 0);
        Assert.True(reports.SequenceEqual(reports.OrderBy(r => r.BytesReceived)));
        var last = reports[^1];
        Assert.Equal(payload.Length, last.BytesReceived);
        Assert.Equal(payload.Length, last.TotalBytes);
    }

    [Fact]
    public async Task Copy_Cancellation_RemovesPartialDestination()
    {
        var payload = new byte[2 * 1024 * 1024];
        await File.WriteAllBytesAsync(Source(), payload);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FileCopy.CopyWithProgressAsync(
                Source(), Destination(),
                new InlineProgress(_ => cancellation.Cancel()),
                cancellationToken: cancellation.Token));

        Assert.False(File.Exists(Destination()));
    }

    [Fact]
    public async Task Copy_MissingSource_ThrowsWithoutDestination()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => FileCopy.CopyWithProgressAsync(Source("missing.iso"), Destination()));

        Assert.False(File.Exists(Destination()));
    }

    private sealed class InlineProgress(Action<RemoteIsoDownloadProgress> action)
        : IProgress<RemoteIsoDownloadProgress>
    {
        public void Report(RemoteIsoDownloadProgress value) => action(value);
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
