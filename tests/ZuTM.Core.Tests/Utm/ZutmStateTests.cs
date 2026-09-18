// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Core.Utm;
using Xunit;

namespace ZuTM.Core.Tests.Utm;

public class ZutmStateTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("zutm-state-tests-").FullName;

    private string StatePath => Path.Combine(_tempRoot, ZutmState.FileName);

    [Fact]
    public void TryLoad_ReturnsNull_WhenFileMissing()
    {
        Assert.Null(ZutmState.TryLoad(StatePath));
    }

    [Fact]
    public void TryLoad_ReturnsNull_WhenFileCorrupt()
    {
        File.WriteAllText(StatePath, "{ not json !!!");
        Assert.Null(ZutmState.TryLoad(StatePath));
    }

    [Fact]
    public void SaveLoad_RoundTripsAllFields()
    {
        var state = new ZutmState
        {
            ExternalDrivePaths = new Dictionary<string, string> { ["drive-1"] = @"D:\images\disk.qcow2" },
            LastStartedUtc = new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero),
            Window = new ZutmWindowState { X = 5, Y = 10, Width = 1280, Height = 800, Maximized = true },
        };

        state.Save(StatePath);
        var loaded = ZutmState.TryLoad(StatePath);

        Assert.NotNull(loaded);
        Assert.Equal(@"D:\images\disk.qcow2", loaded!.ExternalDrivePaths["drive-1"]);
        Assert.Equal(state.LastStartedUtc, loaded.LastStartedUtc);
        Assert.Equal(1280, loaded.Window!.Width);
        Assert.True(loaded.Window.Maximized);
    }

    [Fact]
    public void Save_AlwaysWritesCurrentVersion()
    {
        new ZutmState { Version = 0 }.Save(StatePath);
        var loaded = ZutmState.TryLoad(StatePath);
        Assert.Equal(ZutmState.CurrentVersion, loaded!.Version);
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
