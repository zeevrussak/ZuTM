// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Spice;
using Xunit;

namespace ZuTM.Update.Tests;

public class RemoteViewerArgumentsTests
{
    [Fact]
    public void BuildsSpiceUri()
    {
        var arguments = RemoteViewerArguments.Build("127.0.0.1", 5900);

        Assert.Equal(["spice://127.0.0.1:5900", "--spice-disable-effects=all"], arguments);
    }

    [Fact]
    public void FullScreenAddsKiosk()
    {
        var arguments = RemoteViewerArguments.Build("127.0.0.1", 5901, new SpiceConsoleOptions { FullScreen = true });

        Assert.Contains("--kiosk", arguments);
    }

    [Fact]
    public void DisableEffectsOff_RemovesFlag()
    {
        var arguments = RemoteViewerArguments.Build("127.0.0.1", 5901, new SpiceConsoleOptions { DisableEffects = false });

        Assert.DoesNotContain("--spice-disable-effects=all", arguments);
    }

    [Fact]
    public void ExtraArguments_AppendedVerbatim()
    {
        var arguments = RemoteViewerArguments.Build("127.0.0.1", 5901,
            new SpiceConsoleOptions { ExtraArguments = ["--debug-gtk"] });

        Assert.Equal("--debug-gtk", arguments[^1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void RejectsInvalidPorts(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RemoteViewerArguments.Build("127.0.0.1", port));

    [Fact]
    public void RejectsEmptyHost() =>
        Assert.Throws<ArgumentException>(() => RemoteViewerArguments.Build("", 5900));

    [Fact]
    public void Launcher_WithoutBinary_ThrowsClearError()
    {
        var launcher = new SpiceConsoleLauncher(executablePath: null);
        if (launcher.ExecutablePath is not null)
        {
            return; // virt-viewer installed on this machine; discovery success is fine
        }

        Assert.Throws<FileNotFoundException>(() => launcher.Start("127.0.0.1", 5900));
    }
}
