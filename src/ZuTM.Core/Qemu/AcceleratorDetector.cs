// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Runtime.InteropServices;

namespace ZuTM.Core.Qemu;

public enum QemuAcceleration
{
    /// <summary>Windows Hypervisor Platform — hardware acceleration when guest and host architectures match.</summary>
    Whpx,

    /// <summary>TCG JIT emulation — universal fallback (required for cross-architecture guests).</summary>
    Tcg,
}

/// <summary>
/// Detects the best available QEMU acceleration on this host.
/// WHPX acceleration requires the Windows Hypervisor Platform optional
/// feature and a guest architecture matching the host (x64→x64/x86,
/// ARM64→ARM64); everything else runs under TCG.
/// </summary>
public sealed class AcceleratorDetector
{
    /// <summary>Host CPU architecture as QEMU names it.</summary>
    public static string HostArchitecture => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "aarch64",
        Architecture.X86 => "i386",
        Architecture.Arm => "arm",
        _ => "unknown",
    };

    /// <summary>Returns true when the Windows Hypervisor Platform API reports a present hypervisor.</summary>
    public static bool IsWindowsHypervisorPlatformAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            // WHvCapabilityCodeHypervisorPresent = 0; result is a ULONG boolean.
            return WhvGetCapability(0, out var present, sizeof(ulong), out _) == 0 && present != 0;
        }
        catch (DllNotFoundException)
        {
            return false; // WHPX optional feature not installed
        }
    }

    /// <summary>Picks the acceleration QEMU should use for a guest architecture.</summary>
    /// <param name="guestArchitecture">QEMU guest architecture name.</param>
    /// <param name="whpxAvailable">
    /// Override for the WHPX probe (test injection). Null = probe the real host.
    /// </param>
    public static QemuAcceleration Detect(string guestArchitecture, bool? whpxAvailable = null) =>
        CanHardwareAccelerate(guestArchitecture, whpxAvailable) ? QemuAcceleration.Whpx : QemuAcceleration.Tcg;

    /// <summary>WHPX can only virtualize guests of the host's own architecture class.</summary>
    public static bool CanHardwareAccelerate(string guestArchitecture, bool? whpxAvailable = null)
    {
        var available = whpxAvailable ?? IsWindowsHypervisorPlatformAvailable();
        if (!available)
        {
            return false;
        }

        var host = HostArchitecture;
        return (host, guestArchitecture) switch
        {
            ("x86_64", "x86_64") => true,
            ("x86_64", "i386") => true, // 32-bit x86 guests run under an x64 WHPX partition
            ("aarch64", "aarch64") => true,
            _ => false,
        };
    }

    [DllImport("WinHvPlatform.dll")]
    private static extern int WhvGetCapability(int capabilityCode, out ulong capabilityValue, int capabilityValueSize, out int writtenSize);
}
