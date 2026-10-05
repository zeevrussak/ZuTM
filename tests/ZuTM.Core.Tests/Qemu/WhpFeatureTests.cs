// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Core.Qemu;
using Xunit;

namespace ZuTM.Core.Tests.Qemu;

public class WhpFeatureTests
{
    [Theory]
    [InlineData(0, WhpEnableOutcome.Enabled)]
    [InlineData(3010, WhpEnableOutcome.EnabledRestartRequired)]
    [InlineData(87, WhpEnableOutcome.Failed)] // ERROR_INVALID_PARAMETER
    [InlineData(-1, WhpEnableOutcome.Failed)]
    [InlineData(unchecked((int)0x800f080c), WhpEnableOutcome.Failed)] // unknown feature name
    public void ExitCode_MapsToOutcome(int exitCode, WhpEnableOutcome expected)
    {
        Assert.Equal(expected, WhpFeature.MapExitCode(exitCode));
    }

    [Fact]
    public void EnableArguments_TargetHypervisorPlatform_NeverAutoReboots()
    {
        var arguments = WhpFeature.BuildEnableArguments();

        Assert.Contains("/Online", arguments);
        Assert.Contains("/Enable-Feature", arguments);
        Assert.Contains(WhpFeature.FeatureName, arguments);
        Assert.Equal("HypervisorPlatform", WhpFeature.FeatureName);
        Assert.Contains("/All", arguments);
        // A GUI app must never reboot the user's machine on its own.
        Assert.Contains("/NoRestart", arguments);
    }

    [Fact]
    public void EnablementOffered_OnlyWhileFeatureDisabled()
    {
        Assert.True(WhpFeature.CanOfferEnablement(WhpFeatureStatus.FeatureDisabled));
        Assert.False(WhpFeature.CanOfferEnablement(WhpFeatureStatus.Available));
        Assert.False(WhpFeature.CanOfferEnablement(WhpFeatureStatus.HypervisorInactive));
    }

    [Theory]
    [InlineData(WhpFeatureStatus.FeatureDisabled)]
    [InlineData(WhpFeatureStatus.HypervisorInactive)]
    public void Guidance_NamesTheFeature_AndStatesTheTcgConsequence(WhpFeatureStatus status)
    {
        var guidance = WhpFeature.GetGuidance(status);

        Assert.Contains("Windows Hypervisor Platform", guidance);
        Assert.Contains("TCG", guidance);
    }

    [Fact]
    public void Guidance_ForInactiveHypervisor_SaysRestartFirst_ThenBcdedit()
    {
        var guidance = WhpFeature.GetGuidance(WhpFeatureStatus.HypervisorInactive);

        Assert.Contains("Restart Windows", guidance);
        Assert.Contains("hypervisorlaunchtype", guidance);
    }

    [Fact]
    public void Guidance_ForDisabledFeature_SaysConsentAndRestartRequired()
    {
        var guidance = WhpFeature.GetGuidance(WhpFeatureStatus.FeatureDisabled);

        Assert.Contains("administrator", guidance);
        Assert.Contains("restart", guidance);
    }

    [Fact]
    public void RealHostStatus_NeverThrows_AndAgreesWithTheRawProbe()
    {
        var exception = Record.Exception(() => WhpFeature.GetStatus());
        Assert.Null(exception);

        // Available must mean exactly "the WHv probe sees a hypervisor".
        var status = WhpFeature.GetStatus();
        Assert.Equal(
            AcceleratorDetector.IsWindowsHypervisorPlatformAvailable(),
            status == WhpFeatureStatus.Available);
    }
}
