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

/// <summary>Outcome of probing the Windows Hypervisor Platform API surface.</summary>
internal enum WhvProbe
{
    /// <summary>WinHvPlatform.dll is missing or lacks the WHv exports — the optional feature is off.</summary>
    ApiUnavailable,

    /// <summary>WHv API present but no hypervisor is running (pending restart, hypervisorlaunchtype off, or firmware virtualization disabled).</summary>
    HypervisorAbsent,

    /// <summary>A hypervisor is present; WHPX partitions can be created.</summary>
    HypervisorPresent,
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

    /// <summary>
    /// Returns true when the Windows Hypervisor Platform API reports a present hypervisor.
    /// The probe resolves <c>WhvGetCapability</c> dynamically: on machines without the
    /// WHP optional feature the system DLL exists but does not export the WHv API
    /// (a direct P/Invoke there throws EntryPointNotFoundException), which must read
    /// as "not available", never escape as an error.
    /// </summary>
    public static bool IsWindowsHypervisorPlatformAvailable() => ProbeWhvApi() == WhvProbe.HypervisorPresent;

    /// <summary>
    /// Classifies the WHv API surface so remediation UI can tell "feature off"
    /// (nothing exported to enable) apart from "feature on but hypervisor not
    /// running" (exported, but HypervisorPresent reports false).
    /// </summary>
    internal static WhvProbe ProbeWhvApi()
    {
        if (!OperatingSystem.IsWindows())
        {
            return WhvProbe.ApiUnavailable;
        }

        if (!NativeLibrary.TryLoad("WinHvPlatform.dll", out var handle))
        {
            return WhvProbe.ApiUnavailable; // WHP optional feature absent on this host
        }

        try
        {
            if (!NativeLibrary.TryGetExport(handle, "WhvGetCapability", out var address))
            {
                return WhvProbe.ApiUnavailable; // stub DLL without the WHv exports — feature not enabled
            }

            var whvGetCapability = Marshal.GetDelegateForFunctionPointer<WhvGetCapabilityDelegate>(address);

            // WHvCapabilityCodeHypervisorPresent = 0; result is a ULONG boolean.
            return whvGetCapability(0, out var present, sizeof(ulong), out _) == 0 && present != 0
                ? WhvProbe.HypervisorPresent
                : WhvProbe.HypervisorAbsent;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or BadImageFormatException or MarshalDirectiveException)
        {
            return WhvProbe.ApiUnavailable; // unreadable/unexpected export surface — treat as feature-off
        }
        finally
        {
            NativeLibrary.Free(handle);
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
        return available && CanGuestMatchHostArchitecture(guestArchitecture);
    }

    /// <summary>
    /// Explanation shown to the user when a guest falls back to TCG despite being
    /// hardware-acceleratable in principle (same architecture class as the host) —
    /// i.e. the Windows Hypervisor Platform feature is missing. Null when TCG is
    /// the expected outcome (cross-architecture guest or WHPX working).
    /// </summary>
    public static string? GetTcgFallbackReason(string guestArchitecture, bool? whpxAvailable = null)
    {
        if (!CanGuestMatchHostArchitecture(guestArchitecture))
        {
            return null; // cross-architecture guest: TCG is the only option by design
        }

        var available = whpxAvailable ?? IsWindowsHypervisorPlatformAvailable();
        if (available)
        {
            return null; // WHPX works — nothing to explain
        }

        return "Windows Hypervisor Platform is not available, so this VM runs with software "
            + "emulation (TCG), which is much slower. To enable hardware acceleration, turn on the "
            + "'Windows Hypervisor Platform' feature — see the acceleration notice in the VM details "
            + "pane, or Run: optionalfeatures.exe — then restart Windows.";
    }

    /// <summary>True when the guest architecture class can run under the host's WHPX partition.</summary>
    public static bool CanGuestMatchHostArchitecture(string guestArchitecture)
    {
        var host = HostArchitecture;
        return (host, guestArchitecture) switch
        {
            ("x86_64", "x86_64") => true,
            ("x86_64", "i386") => true, // 32-bit x86 guests run under an x64 WHPX partition
            ("aarch64", "aarch64") => true,
            _ => false,
        };
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WhvGetCapabilityDelegate(
        int capabilityCode, out ulong capabilityValue, int capabilityValueSize, out int writtenSize);
}
