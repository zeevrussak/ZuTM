// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — Apache License 2.0.

using Xunit;

namespace ZuTM.Update.Tests;

/// <summary>
/// Windows Installer exit codes map to the outcomes the app reports to the
/// user — a cancelled or failed update must be surfaced, never silent.
/// </summary>
public class UpdateInstallRunnerTests
{
    [Theory]
    [InlineData(0, InstallerOutcome.Success)]
    [InlineData(3010, InstallerOutcome.SuccessRebootRequired)]
    [InlineData(1602, InstallerOutcome.UserCancelled)] // UAC declined / wizard Cancel
    [InlineData(1603, InstallerOutcome.Failed)]        // fatal install error
    [InlineData(1618, InstallerOutcome.Failed)]        // another install in progress
    [InlineData(1638, InstallerOutcome.Failed)]        // another version installed
    [InlineData(-1, InstallerOutcome.Failed)]
    public void ClassifyExitCode_MapsWindowsInstallerCodes(int exitCode, InstallerOutcome expected)
    {
        Assert.Equal(expected, UpdateInstallRunner.ClassifyExitCode(exitCode));
    }

    [Fact]
    public void FailedResult_SuggestsTheInstallerLog()
    {
        var result = new UpdateInstallResult(UpdateInstallRunner.ClassifyExitCode(1603), 1603);

        Assert.True(result.ShowLogHint);
        Assert.False(new UpdateInstallResult(InstallerOutcome.Success, 0).ShowLogHint);
    }
}
