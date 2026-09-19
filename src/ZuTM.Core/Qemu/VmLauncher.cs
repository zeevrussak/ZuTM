// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
// Single start path shared by the GUI and the `zutm` CLI: build the plan,
// seed UEFI vars, launch QEMU, connect QMP, record the runtime registry.

using ZuTM.Core.Utm;

namespace ZuTM.Core.Qemu;

public sealed record VmLaunchResult(QemuVmProcess Process, QemuPortSet Ports);

public sealed class VmLauncher(QemuRuntime runtime, RuntimeRegistry? registry = null)
{
    public QemuRuntime Runtime { get; } = runtime ?? throw new ArgumentNullException(nameof(runtime));

    /// <summary>Builds the launch plan for a bundle (pure — used by tests and dry-runs).</summary>
    public QemuLaunchPlan BuildPlan(UtmBundle bundle, QemuPortSet ports, QemuAcceleration? acceleration = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        string? efiVarsPath = null;
        if (bundle.Configuration.Qemu.HasUefiBoot)
        {
            efiVarsPath = Path.Combine(bundle.DataDirectory, UtmBundleFiles.EfiVariables);
            if (!File.Exists(efiVarsPath))
            {
                // 1 MiB blank pflash: QEMU initializes UEFI variables on first boot.
                File.WriteAllBytes(efiVarsPath, new byte[1024 * 1024]);
            }
        }

        return new QemuCommandLineBuilder(
            bundle.Configuration,
            Runtime,
            ports,
            bundle.ResolveDriveImagePath,
            acceleration,
            efiVarsPath).Build();
    }

    /// <summary>Starts a VM: plan → QEMU process → QMP connect → registry record.</summary>
    public async Task<VmLaunchResult> StartAsync(UtmBundle bundle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var ports = PortAllocator.AllocateForLaunch(bundle.Configuration.Serials.Count);
        var plan = BuildPlan(bundle, ports);

        string? logPath = bundle.Configuration.Qemu.HasDebugLog
            ? Path.Combine(bundle.DataDirectory, UtmBundleFiles.DebugLog)
            : null;

        var process = await QemuVmProcess.StartAsync(plan, logPath, cancellationToken);

        registry?.RecordStart(new VmRuntimeEntry
        {
            Uuid = bundle.Id.ToString("D"),
            Name = bundle.Configuration.Information.Name,
            Pid = process.ProcessId,
            QmpPort = ports.QmpPort,
            SpicePort = ports.SpicePort,
            StartedUtc = DateTimeOffset.UtcNow,
        });

        return new VmLaunchResult(process, ports);
    }

    /// <summary>Connects to a VM recorded in the runtime registry (CLI path).</summary>
    public static async Task<QmpClient> ConnectRecordedAsync(VmRuntimeEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return await QmpClient.ConnectAsync("127.0.0.1", entry.QmpPort, cancellationToken);
    }
}
