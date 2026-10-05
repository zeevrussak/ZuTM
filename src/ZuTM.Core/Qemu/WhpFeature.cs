// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.ComponentModel;
using System.Diagnostics;

namespace ZuTM.Core.Qemu;

/// <summary>Host state of the Windows Hypervisor Platform optional feature.</summary>
public enum WhpFeatureStatus
{
    /// <summary>WHPX works — a hypervisor is present and partitions can be created.</summary>
    Available,

    /// <summary>The 'Windows Hypervisor Platform' optional feature is off — the WHv API is not exported.</summary>
    FeatureDisabled,

    /// <summary>The feature is on, but no hypervisor is running: a pending restart, 'hypervisorlaunchtype off',
    /// or firmware virtualization disabled.</summary>
    HypervisorInactive,
}

/// <summary>Result of an attempt to enable the feature via an elevated process.</summary>
public enum WhpEnableOutcome
{
    /// <summary>DISM enabled the feature and the change is already live (rare).</summary>
    Enabled,

    /// <summary>DISM enabled the feature; Windows must restart before the hypervisor runs.</summary>
    EnabledRestartRequired,

    /// <summary>The user declined the elevation (UAC) prompt — nothing was changed.</summary>
    UserCancelled,

    /// <summary>DISM could not enable the feature; its exit code is in <see cref="WhpEnableResult.ExitCode"/>.</summary>
    Failed,
}

/// <summary>Outcome of one enablement attempt, with the raw DISM exit code for diagnostics.</summary>
public readonly record struct WhpEnableResult(WhpEnableOutcome Outcome, int ExitCode);

/// <summary>
/// Status reporting and one-click enablement for the Windows Hypervisor Platform
/// optional feature that WHPX acceleration requires. State is classified from the
/// WHv API surface (see <see cref="AcceleratorDetector.ProbeWhvApi"/>); enabling
/// runs the documented DISM command with the 'runas' verb, so Windows collects
/// administrator consent via its own UAC prompt and ZuTM itself never needs to
/// run elevated.
/// </summary>
public static class WhpFeature
{
    /// <summary>CBS name of the optional feature backing WHPX.</summary>
    public const string FeatureName = "HypervisorPlatform";

    /// <summary>DISM exit code for success with a pending reboot (ERROR_SUCCESS_REBOOT_REQUIRED).</summary>
    public const int ExitCodeRestartRequired = 3010;

    /// <summary>Probes the host and classifies why WHPX is or is not usable right now.</summary>
    public static WhpFeatureStatus GetStatus() => AcceleratorDetector.ProbeWhvApi() switch
    {
        WhvProbe.HypervisorPresent => WhpFeatureStatus.Available,
        WhvProbe.HypervisorAbsent => WhpFeatureStatus.HypervisorInactive,
        _ => WhpFeatureStatus.FeatureDisabled,
    };

    /// <summary>True when the in-app enablement offer makes sense — only a disabled feature can be turned on.</summary>
    public static bool CanOfferEnablement(WhpFeatureStatus status) => status == WhpFeatureStatus.FeatureDisabled;

    /// <summary>User-facing explanation of a status, phrased for the VM-details acceleration notice.</summary>
    public static string GetGuidance(WhpFeatureStatus status) => status switch
    {
        WhpFeatureStatus.FeatureDisabled =>
            "This VM runs with software emulation (TCG), which is much slower, because the Windows "
            + "Hypervisor Platform feature is not enabled. Enabling it asks for administrator consent "
            + "and requires a Windows restart; hardware acceleration then applies from the next VM start.",
        WhpFeatureStatus.HypervisorInactive =>
            "The Windows Hypervisor Platform feature is enabled, but no hypervisor is running, so this VM "
            + "falls back to software emulation (TCG). Restart Windows to finish enabling it. If this notice "
            + "persists, run 'bcdedit /set hypervisorlaunchtype auto' in an elevated terminal and check that "
            + "virtualization is enabled in the firmware settings.",
        WhpFeatureStatus.Available =>
            "Windows Hypervisor Platform is enabled — VMs of this architecture run with hardware acceleration (WHPX).",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>The DISM command line <see cref="EnableElevated"/> runs (public for transparency and tests).</summary>
    public static string BuildEnableArguments() =>
        $"/Online /Enable-Feature /FeatureName:{FeatureName} /All /NoRestart";

    /// <summary>Maps a DISM exit code to the enablement outcome.</summary>
    public static WhpEnableOutcome MapExitCode(int exitCode) => exitCode switch
    {
        0 => WhpEnableOutcome.Enabled,
        ExitCodeRestartRequired => WhpEnableOutcome.EnabledRestartRequired,
        _ => WhpEnableOutcome.Failed,
    };

    /// <summary>
    /// Enables the feature by launching DISM elevated: the OS shows the UAC consent
    /// prompt, and DISM's own console window reports progress. Blocks until DISM
    /// exits, so callers should invoke it from a background thread. /NoRestart keeps
    /// Windows from rebooting the machine without the user's express consent.
    /// </summary>
    public static WhpEnableResult EnableElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WhpEnableResult(WhpEnableOutcome.Failed, -1);
        }

        try
        {
            var startInfo = new ProcessStartInfo("dism.exe")
            {
                Arguments = BuildEnableArguments(),
                UseShellExecute = true,
                Verb = "runas",
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new WhpEnableResult(WhpEnableOutcome.Failed, -1);
            }

            process.WaitForExit();
            return new WhpEnableResult(MapExitCode(process.ExitCode), process.ExitCode);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED — UAC prompt declined
        {
            return new WhpEnableResult(WhpEnableOutcome.UserCancelled, ex.NativeErrorCode);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WHP enablement failed to launch: {ex.Message}");
            return new WhpEnableResult(WhpEnableOutcome.Failed, -1);
        }
    }
}
