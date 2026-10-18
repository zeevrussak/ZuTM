// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — Apache License 2.0.

using ZuTM.Core.Diagnostics;
using Xunit;

namespace ZuTM.Core.Tests.Diagnostics;

public class CrashLogTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("zutm-crashlog-test-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Write_CreatesDirectoryAndFile()
    {
        var nested = Path.Combine(_directory, "does", "not", "exist");
        var log = new CrashLog(nested);

        log.Write("first");

        Assert.True(File.Exists(Path.Combine(nested, "crash.log")));
        Assert.Contains("first", File.ReadAllText(Path.Combine(nested, "crash.log")));
    }

    [Fact]
    public void Write_IncludesExceptionTypeAndMessage()
    {
        var log = new CrashLog(_directory);

        log.Write("unhandled", new InvalidOperationException("the exact reason"));

        var content = File.ReadAllText(Path.Combine(_directory, "crash.log"));
        Assert.Contains("unhandled", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("the exact reason", content);
    }

    [Fact]
    public void Write_WithoutException_NotesAbsence()
    {
        var log = new CrashLog(_directory);

        log.Write("mystery", exception: null);

        Assert.Contains("<no exception detail>", File.ReadAllText(Path.Combine(_directory, "crash.log")));
    }

    [Fact]
    public void Write_RotatesArchiveWhenOverLimit()
    {
        var log = new CrashLog(_directory, maxBytes: 128);
        log.Write("first generation");

        // A single entry big enough to push the file over the 128-byte cap,
        // then one more: the size check runs before each append, so the
        // rotation happens when the *next* entry arrives.
        log.Write(new string('x', 256));
        log.Write("second generation");

        Assert.Contains("first generation", File.ReadAllText(log.ArchivePath));
        var fresh = File.ReadAllText(log.FilePath);
        Assert.Contains("second generation", fresh);
        Assert.DoesNotContain("first generation", fresh);
    }

    [Fact]
    public void Write_NeverThrows_WhenDirectoryPathIsOccupiedByAFile()
    {
        var occupied = Path.Combine(_directory, "occupied");
        File.WriteAllText(occupied, "a file, not a directory");
        var log = new CrashLog(Path.Combine(occupied, "crash.log".Replace(".log", "")));

        log.Write("must not throw");

        // Nothing to assert beyond survival — the write is best-effort.
    }

    [Fact]
    public void Write_IsThreadSafe_EveryEntryLands()
    {
        var log = new CrashLog(_directory);
        const int threads = 8;
        const int writesPerThread = 25;

        Parallel.For(0, threads, thread =>
        {
            for (var i = 0; i < writesPerThread; i++)
            {
                log.Write($"t{thread}-w{i}");
            }
        });

        var content = File.ReadAllText(Path.Combine(_directory, "crash.log"));
        for (var thread = 0; thread < threads; thread++)
        {
            for (var i = 0; i < writesPerThread; i++)
            {
                Assert.Contains($"t{thread}-w{i}", content);
            }
        }
    }
}
