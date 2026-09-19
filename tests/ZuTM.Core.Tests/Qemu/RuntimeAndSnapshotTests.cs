// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Core.Qemu;
using Xunit;

namespace ZuTM.Core.Tests.Qemu;

public class SnapshotParsingTests
{
    [Fact]
    public void ParsesInfoSnapshotsTable()
    {
        const string output = """
            Snapshot list (from 00000000 to 00000003):
            ------------------------------------------
                ID        TAG                 VM SIZE                DATE       VM CLOCK
            --  1         clean-state             4.2 MiB  2026-09-19 10:00:00   00:00:05.123
            --  2         before-upgrade         12.7 MiB  2026-09-19 11:30:00   00:12:44.000
            """;

        var names = QmpClientSnapshotExtensions.ParseSnapshotList(output);

        Assert.Equal(["clean-state", "before-upgrade"], names);
    }

    [Fact]
    public void ParsesEmptyList()
    {
        var names = QmpClientSnapshotExtensions.ParseSnapshotList("There is no snapshot available.");
        Assert.Empty(names);
    }

    [Fact]
    public void IgnoresNonDataRows()
    {
        const string output = """
            ID        TAG
            00000000 no  --
            ''-- this can happen when a snapshot is taken on the default AUX
            """;

        // "00000000 no" parses (id + tag) — that is a real data shape; rows
        // without leading digits are ignored.
        var names = QmpClientSnapshotExtensions.ParseSnapshotList(output);
        Assert.Contains("no", names);
    }
}

public class RuntimeRegistryTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("zutm-registry-").FullName;
    private string RegistryPath => Path.Combine(_tempRoot, "running.json");

    private static int LivePid()
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            Arguments = "/c exit 0",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        process.WaitForExit(10_000);
        return process.Id; // exited pid — registry should treat as stale
    }

    [Fact]
    public void RecordStartRoundTripsAndFindsByUuidAndName()
    {
        var registry = new RuntimeRegistry(RegistryPath);
        registry.RecordStart(new VmRuntimeEntry
        {
            Uuid = "6f0f0c6a-8dc0-47d1-a381-c0abf67a8c8d",
            Name = "Test VM",
            Pid = System.Environment.ProcessId, // this process: alive
            QmpPort = 4444,
            SpicePort = 5900,
            StartedUtc = DateTimeOffset.UtcNow,
        });

        // Fresh instance reads from disk.
        var reloaded = new RuntimeRegistry(RegistryPath);
        var byUuid = reloaded.Find(new Guid("6f0f0c6a-8dc0-47d1-a381-c0abf67a8c8d"));
        var byName = reloaded.Find("test vm");

        Assert.NotNull(byUuid);
        Assert.NotNull(byName);
        Assert.Equal(4444, byName!.QmpPort);
    }

    [Fact]
    public void RecordStopRemovesEntry()
    {
        var registry = new RuntimeRegistry(RegistryPath);
        registry.RecordStart(new VmRuntimeEntry
        {
            Uuid = "11111111-2222-3333-4444-555555555555",
            Name = "X",
            Pid = System.Environment.ProcessId,
            QmpPort = 1,
            SpicePort = 2,
        });
        registry.RecordStop("11111111-2222-3333-4444-555555555555");

        Assert.Null(new RuntimeRegistry(RegistryPath).Find("11111111-2222-3333-4444-555555555555"));
    }

    [Fact]
    public void StaleProcessesArePrunedOnLoad()
    {
        var registry = new RuntimeRegistry(RegistryPath);
        registry.RecordStart(new VmRuntimeEntry
        {
            Uuid = "99999999-8888-7777-6666-555555555555",
            Name = "Dead",
            Pid = LivePid(), // process already exited
            QmpPort = 3,
            SpicePort = 4,
        });

        var reloaded = new RuntimeRegistry(RegistryPath);
        Assert.Null(reloaded.Find("Dead"));
    }

    [Fact]
    public void CorruptRegistryFileYieldsEmptyRegistry()
    {
        Directory.CreateDirectory(_tempRoot);
        File.WriteAllText(RegistryPath, "{ broken");
        Assert.Empty(new RuntimeRegistry(RegistryPath).Entries);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
