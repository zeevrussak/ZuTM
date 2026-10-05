// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
// Maps a UTM QEMU configuration onto a QEMU command line for Windows hosts
// (WHPX/TCG acceleration, TCP-based SPICE/QMP/serial endpoints instead of
// Unix sockets, DirectX Sound audio). Mirrors UTM's semantics where the
// platforms agree; Windows divergences produce Warnings, never silent
// behavior changes.

using ZuTM.Core.Net;
using ZuTM.Core.Utm;

namespace ZuTM.Core.Qemu;

/// <summary>TCP endpoints allocated for one VM launch.</summary>
public sealed record QemuPortSet
{
    public required int SpicePort { get; init; }

    public required int QmpPort { get; init; }

    public int GuestAgentPort { get; init; }

    /// <summary>TCP port per serial index.</summary>
    public IReadOnlyDictionary<int, int> SerialPorts { get; init; } =
        new Dictionary<int, int>();

    public static QemuPortSet ForTesting(int spicePort = 5900, int qmpPort = 4444) => new()
    {
        SpicePort = spicePort,
        QmpPort = qmpPort,
    };
}

/// <summary>A fully resolved QEMU invocation.</summary>
public sealed record QemuLaunchPlan
{
    public required string ExecutablePath { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public required QemuPortSet Ports { get; init; }

    /// <summary>Human-readable notes about settings that Windows QEMU cannot honor as-is.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string CommandLine => Arguments.Aggregate(
        $"\"{ExecutablePath}\"",
        (current, argument) => current + " " + QuoteArgument(argument));

    private static string QuoteArgument(string argument)
    {
        // QEMU interprets ',' and '=' inside option arguments; values containing
        // spaces or commas are quoted so argv survives process spawning intact.
        return argument.Contains(' ') && !argument.StartsWith('"')
            ? $"\"{argument}\""
            : argument;
    }
}

/// <summary>
/// Builds the QEMU command line for a UTM configuration. Pure: image path
/// resolution is injected, no I/O happens here, and identical inputs yield
/// identical plans — which is what the unit tests pin down.
/// </summary>
public sealed class QemuCommandLineBuilder
{
    private readonly UtmConfiguration _configuration;
    private readonly QemuRuntime _runtime;
    private readonly QemuPortSet _ports;
    private readonly Func<UtmDrive, string?> _resolveImagePath;
    private readonly QemuAcceleration _acceleration;
    private readonly string? _efiVarsPath;
    private readonly bool _ensureUsbBus;
    private readonly List<string> _arguments = [];
    private readonly List<string> _warnings = [];

    public QemuCommandLineBuilder(
        UtmConfiguration configuration,
        QemuRuntime runtime,
        QemuPortSet ports,
        Func<UtmDrive, string?>? resolveImagePath = null,
        QemuAcceleration? acceleration = null,
        string? efiVarsPath = null,
        bool? whpxAvailable = null,
        bool ensureUsbBus = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(ports);
        _configuration = configuration;
        _runtime = runtime;
        _ports = ports;
        _resolveImagePath = resolveImagePath ?? (_ => null);
        _efiVarsPath = efiVarsPath;
        _ensureUsbBus = ensureUsbBus;
        if (acceleration is not null)
        {
            _acceleration = acceleration.Value;
        }
        else
        {
            _acceleration = AcceleratorDetector.Detect(configuration.System.Architecture, whpxAvailable);
            var fallbackReason = AcceleratorDetector.GetTcgFallbackReason(configuration.System.Architecture, whpxAvailable);
            if (fallbackReason is not null)
            {
                // The user gets hardware-quality speed only with WHPX; a silent
                // TCG fallback reads as "ZuTM is broken" — explain instead.
                _warnings.Add(fallbackReason);
            }
        }
    }

    public QemuLaunchPlan Build()
    {
        AddMachineArguments();
        AddCpuArguments();
        AddMemoryArguments();
        AddFirmwareArguments();
        AddDriveArguments();
        AddNetworkArguments();
        AddDisplayArguments();
        AddSoundArguments();
        AddInputArguments();
        AddSerialArguments();
        AddDeviceArguments();
        AddSharingArguments();
        AddControlArguments();
        AddAdditionalArguments();

        return new QemuLaunchPlan
        {
            ExecutablePath = _runtime.SystemExecutable(_configuration.System.Architecture),
            Arguments = [.. _arguments],
            Ports = _ports,
            Warnings = [.. _warnings],
        };
    }

    private void AddMachineArguments()
    {
        var info = _configuration.Information;
        _arguments.Add("-name");
        _arguments.Add(info.Name);
        _arguments.Add("-uuid");
        _arguments.Add(info.Uuid.ToString("D"));

        var machine = _configuration.System.Target;
        if (!string.IsNullOrWhiteSpace(_configuration.Qemu.MachinePropertyOverride))
        {
            machine += "," + _configuration.Qemu.MachinePropertyOverride;
        }

        _arguments.Add("-machine");
        _arguments.Add(machine);

        _arguments.Add("-accel");
        if (_acceleration == QemuAcceleration.Whpx)
        {
            _arguments.Add("whpx");
        }
        else
        {
            // Single vCPU (or count of 1) runs single-threaded TCG like UTM;
            // multiple vCPUs use multi-threaded TCG unless correctness is forced.
            var multiThread = _configuration.System.CpuCount is > 1 or 0
                && !_configuration.System.ForceMulticore;
            _arguments.Add(multiThread ? "tcg,thread=multi" : "tcg,thread=single");
        }

        // Firmware search path for bundled BIOS/UEFI blobs.
        _arguments.Add("-L");
        _arguments.Add(_runtime.ShareDirectory);

        if (_configuration.Qemu.HasRtcLocalTime)
        {
            _arguments.Add("-rtc");
            _arguments.Add("base=localtime");
        }

        // virtio-serial bus before any virtserialport (vdagent, webdav, guest
        // agent) — headless VMs with a guest agent need it too.
        if (_configuration.Displays.Count > 0 || _ports.GuestAgentPort > 0)
        {
            _arguments.Add("-device");
            _arguments.Add("virtio-serial-pci");
        }
    }

    private void AddCpuArguments()
    {
        var system = _configuration.System;

        if (!string.Equals(system.Cpu, "default", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(system.Cpu))
        {
            var cpu = system.Cpu;
            foreach (var flag in system.CpuFlagsAdd)
            {
                cpu += ",+" + flag;
            }

            foreach (var flag in system.CpuFlagsRemove)
            {
                cpu += ",-" + flag;
            }

            _arguments.Add("-cpu");
            _arguments.Add(cpu);
        }
        else if (system.CpuFlagsAdd.Count > 0 || system.CpuFlagsRemove.Count > 0)
        {
            _warnings.Add("CPU flags are set but the CPU model is 'default'; flags require an explicit CPU model and were ignored.");
        }

        var smp = system.CpuCount > 0 ? system.CpuCount : Environment.ProcessorCount;
        _arguments.Add("-smp");
        _arguments.Add(smp.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void AddMemoryArguments()
    {
        _arguments.Add("-m");
        _arguments.Add(Math.Max(1, _configuration.System.MemorySizeMib)
            .ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void AddFirmwareArguments()
    {
        if (!_configuration.Qemu.HasUefiBoot)
        {
            return;
        }

        var firmware = _runtime.FindUefiFirmware(_configuration.System.Architecture);
        if (firmware is null)
        {
            _warnings.Add("UEFI boot is enabled but no EDK2 firmware is installed in the QEMU runtime; booting will fall back to BIOS.");
            return;
        }

        _arguments.Add("-drive");
        _arguments.Add($"if=pflash,format=raw,readonly=on,file={firmware}");

        if (_efiVarsPath is not null)
        {
            _arguments.Add("-drive");
            _arguments.Add($"if=pflash,format=raw,file={_efiVarsPath}");
        }
    }

    private void AddDriveArguments()
    {
        // Boot priority follows the drive list order (UTM semantics): drives
        // boot in the order they appear, via QEMU bootindex. The counter
        // advances for every bootable drive so unannotated backends (exotic
        // interfaces without an explicit frontend device) can never overtake
        // an annotated one.
        var bootIndex = 0;
        var index = 0;
        foreach (var drive in _configuration.Drives)
        {
            var path = _resolveImagePath(drive);
            var bootable = drive.ImageType is UtmValues.DriveImageType.Disk or UtmValues.DriveImageType.Cd;
            AddDrive(drive, path, index, bootable ? bootIndex : null);
            if (bootable)
            {
                bootIndex++;
            }

            index++;
        }
    }

    private void AddDrive(UtmDrive drive, string? path, int index, int? bootIndex)
    {
        // -bios/-kernel/-initrd/-dtb only accept local files; network streaming
        // (QEMU curl block driver) works for Disk/CD images only.
        if (path is not null
            && drive.ImageType is UtmValues.DriveImageType.Bios or UtmValues.DriveImageType.LinuxKernel
                or UtmValues.DriveImageType.LinuxInitrd or UtmValues.DriveImageType.LinuxDtb
            && RemoteImage.IsRemoteLocation(path))
        {
            _warnings.Add($"Drive {index} ({drive.ImageType}) points at a network URL; QEMU cannot stream this image type and it was skipped.");
            return;
        }

        switch (drive.ImageType)
        {
            case UtmValues.DriveImageType.Bios:
                if (path is not null)
                {
                    _arguments.Add("-bios");
                    _arguments.Add(path);
                }

                break;
            case UtmValues.DriveImageType.LinuxKernel:
                if (path is not null)
                {
                    _arguments.Add("-kernel");
                    _arguments.Add(path);
                }

                break;
            case UtmValues.DriveImageType.LinuxInitrd:
                if (path is not null)
                {
                    _arguments.Add("-initrd");
                    _arguments.Add(path);
                }

                break;
            case UtmValues.DriveImageType.LinuxDtb:
                if (path is not null)
                {
                    _arguments.Add("-dtb");
                    _arguments.Add(path);
                }

                break;
            case UtmValues.DriveImageType.Disk:
                AddDiskDrive(drive, path, index, bootIndex);
                break;
            case UtmValues.DriveImageType.Cd:
                AddCdDrive(drive, path, index, bootIndex);
                break;
            case UtmValues.DriveImageType.None:
                break;
            default:
                _warnings.Add($"Drive {index} has unknown image type '{drive.ImageType}' and was skipped.");
                break;
        }
    }

    private void AddDiskDrive(UtmDrive drive, string? path, int index, int? bootIndex)
    {
        if (path is null)
        {
            _warnings.Add($"Disk drive {index} ('{drive.ImageName ?? drive.Identifier}') has no resolvable image and was skipped.");
            return;
        }

        var isRemote = RemoteImage.IsRemoteLocation(path);
        // Strip any query string so extension detection sees the image name.
        var format = DiskImageExtensions.QemuFormatForFileName(
            isRemote ? path.Split('?', '#')[0] : path);
        var id = $"zutm-drive-{index}";
        string option;
        string? frontendDevice;

        if (drive.Interface is UtmValues.DriveInterface.Ide)
        {
            // Explicit IDE frontend so the disk participates in bootindex
            // ordering (a `if=ide` backend device cannot carry a bootindex).
            option = $"id={id},if=none,format={format},{FileOption(path)}";
            frontendDevice = $"ide-hd,drive={id}";
        }
        else
        {
            var @if = drive.Interface switch
            {
                UtmValues.DriveInterface.Scsi => "scsi",
                UtmValues.DriveInterface.Sd => "sd",
                UtmValues.DriveInterface.Mtd => "mtd",
                UtmValues.DriveInterface.Floppy => "floppy",
                UtmValues.DriveInterface.Pflash => "pflash",
                _ => "none",
            };
            option = $"id={id},if={@if},format={format},{FileOption(path)}";
            frontendDevice = drive.Interface switch
            {
                UtmValues.DriveInterface.Virtio => $"virtio-blk-pci,drive={id}",
                UtmValues.DriveInterface.Nvme => $"nvme,drive={id}",
                UtmValues.DriveInterface.Usb => $"usb-storage,drive={id}",
                _ => null, // backend-provided bus (scsi/sd/mtd/floppy/pflash)
            };
        }

        if (drive.IsReadOnly || isRemote)
        {
            // The curl block driver cannot write: a streaming image is
            // read-only whether or not the drive asked for it.
            option += ",readonly=on";
            if (!drive.IsReadOnly)
            {
                _warnings.Add($"Disk drive {index} streams its image from {ImageHost(path)}; network images are read-only.");
            }
        }

        _arguments.Add("-drive");
        _arguments.Add(option);

        if (frontendDevice is not null)
        {
            if (bootIndex is not null)
            {
                frontendDevice += $",bootindex={bootIndex.Value}";
            }

            _arguments.Add("-device");
            _arguments.Add(frontendDevice);
        }
    }

    private void AddCdDrive(UtmDrive drive, string? path, int index, int? bootIndex)
    {
        var id = $"zutm-cd-{index}";
        string option;
        string? frontendDevice;
        if (drive.Interface == UtmValues.DriveInterface.Scsi)
        {
            // Legacy backend-created SCSI bus (creates the default adapter);
            // the backend device cannot carry a bootindex.
            option = $"id={id},if=scsi,media=cdrom";
            frontendDevice = null;
        }
        else
        {
            option = $"id={id},if=none,media=cdrom";
            frontendDevice = drive.Interface switch
            {
                // No IDE/SATA on `virt` machines: a SCSI CD behind a virtio-scsi
                // controller is the UTM-equivalent CD transport there.
                UtmValues.DriveInterface.Virtio => $"scsi-cd,drive={id},bus=zutm-scsi-{index}.0",
                UtmValues.DriveInterface.Usb => $"usb-storage,drive={id},removable=on",
                // IDE/None: explicit IDE CD frontend (q35's AHCI provides the bus).
                _ => $"ide-cd,drive={id}",
            };
            // Stable frontend id — QMP blockdev-insert/remove-medium (runtime ISO
            // mount/eject in the VM viewer) addresses the tray through it.
            frontendDevice += $",id=zutm-cd-dev-{index}";
        }

        if (path is not null)
        {
            option += $",{FileOption(path)}";
        }

        if (drive.IsReadOnly || RemoteImage.IsRemoteLocation(path))
        {
            option += ",readonly=on";
        }

        if (path is not null && RemoteImage.IsRemoteLocation(path))
        {
            _warnings.Add($"CD drive {index} streams its installer ISO from {ImageHost(path)} over the network — booting and installing run at network speed and need connectivity.");
        }

        _arguments.Add("-drive");
        _arguments.Add(option);

        if (drive.Interface == UtmValues.DriveInterface.Virtio)
        {
            _arguments.Add("-device");
            _arguments.Add($"virtio-scsi-pci,id=zutm-scsi-{index}");
        }

        if (frontendDevice is not null)
        {
            if (bootIndex is not null)
            {
                frontendDevice += $",bootindex={bootIndex.Value}";
            }

            _arguments.Add("-device");
            _arguments.Add(frontendDevice);
        }
    }

    /// <summary>
    /// The -drive file option for one image: a plain local path, or the dotted
    /// sub-options that route a network URL through QEMU's curl block driver
    /// so the guest streams blocks straight off the distributor's server.
    /// </summary>
    private static string FileOption(string path) =>
        RemoteImage.IsRemoteLocation(path)
            ? $"file.driver={RemoteImage.QemuBlockDriver(path)},file.url={path}"
            : $"file={path}";

    private static string ImageHost(string location) =>
        Uri.TryCreate(location, UriKind.Absolute, out var uri) ? uri.Host : location;

    private void AddNetworkArguments()
    {
        for (var i = 0; i < _configuration.Networks.Count; i++)
        {
            var network = _configuration.Networks[i];
            var netdevId = $"zutm-net-{i}";
            var netdev = network.Mode switch
            {
                UtmValues.NetworkMode.Shared => BuildUserNetdev(network, netdevId, restrict: false),
                UtmValues.NetworkMode.Host => BuildUserNetdev(network, netdevId, restrict: true),
                UtmValues.NetworkMode.Emulated => BuildUserNetdev(network, netdevId, restrict: true),
                UtmValues.NetworkMode.Bridged => null,
                _ => null,
            };

            if (netdev is null)
            {
                _warnings.Add($"Network {i} uses mode '{network.Mode}' ({(network.Mode == UtmValues.NetworkMode.Bridged ? "bridged/tap" : "unknown")}); Windows QEMU has no bundled bridge backend, falling back to shared NAT.");
                netdev = BuildUserNetdev(network, netdevId, restrict: false);
            }

            _arguments.Add("-netdev");
            _arguments.Add(netdev);

            var device = $"{network.Hardware},netdev={netdevId}";
            if (!string.IsNullOrWhiteSpace(network.MacAddress))
            {
                device += $",mac={network.MacAddress}";
            }

            _arguments.Add("-device");
            _arguments.Add(device);
        }
    }

    private static string BuildUserNetdev(UtmNetwork network, string id, bool restrict)
    {
        var netdev = $"user,id={id}";
        foreach (var forward in network.PortForward)
        {
            var guest = string.IsNullOrWhiteSpace(forward.GuestAddress)
                ? forward.GuestPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"{forward.GuestAddress}:{forward.GuestPort}";
            netdev += $",hostfwd={forward.Protocol.ToLowerInvariant()}:{forward.HostAddress}:{forward.HostPort}:{guest}";
        }

        if (restrict || network.IsIsolateFromHost)
        {
            netdev += ",restrict=on";
        }

        return netdev;
    }

    private void AddDisplayArguments()
    {
        if (_configuration.Displays.Count == 0)
        {
            // Headless: serial/monitor still exposed over TCP.
            _arguments.Add("-display");
            _arguments.Add("none");
            return;
        }

        foreach (var display in _configuration.Displays)
        {
            _arguments.Add("-device");
            _arguments.Add(string.IsNullOrWhiteSpace(display.Hardware)
                ? "virtio-gpu-pci"
                : display.Hardware);
        }

        // SPICE server on loopback; the console client attaches to it.
        // (The virtio-serial bus is declared in the machine section.)
        _arguments.Add("-spice");
        _arguments.Add($"port={_ports.SpicePort},addr=127.0.0.1,disable-ticketing=on,image-compression=off,playback-compression=off,streaming-video=off");

        // SPICE vdagent enables dynamic resolution, clipboard sync and absolute mouse.
        _arguments.Add("-chardev");
        _arguments.Add("spicevmc,id=zutm-vdagent,name=vdagent");
        _arguments.Add("-device");
        _arguments.Add("virtserialport,chardev=zutm-vdagent,name=com.redhat.spice.0");
    }

    private void AddSoundArguments()
    {
        foreach (var sound in _configuration.Sounds)
        {
            if (string.IsNullOrWhiteSpace(sound.Hardware))
            {
                continue;
            }

            _arguments.Add("-device");
            _arguments.Add(sound.Hardware);
        }
    }

    private void AddInputArguments()
    {
        var input = _configuration.Input;
        if (_configuration.Displays.Count == 0)
        {
            // Headless VMs get no input devices — but a VM that routes USB
            // host devices still needs a controller. It must be cold-plugged:
            // q35's pcie.0 rejects runtime controller hot-plug entirely.
            if (_ensureUsbBus)
            {
                _arguments.Add("-device");
                _arguments.Add("qemu-xhci");
            }

            return;
        }

        // `virt` machines ship no built-in USB controller, so bare `-usb`
        // provides no bus and usb-tablet fails to attach — an xHCI controller
        // is required there. PC machines (q35/pc) have a native USB bus.
        var isVirtMachine = string.Equals(
            _configuration.System.Target, "virt", StringComparison.OrdinalIgnoreCase);
        if (isVirtMachine)
        {
            _arguments.Add("-device");
            _arguments.Add("qemu-xhci");
        }
        else
        {
            _arguments.Add("-usb");
            switch (input.UsbBusSupport)
            {
                case UtmValues.UsbBusSupport.Usb3:
                    _arguments.Add("-device");
                    _arguments.Add("qemu-xhci");
                    break;
                case UtmValues.UsbBusSupport.Usb2:
                    _arguments.Add("-device");
                    _arguments.Add("usb-ehci");
                    break;
                case UtmValues.UsbBusSupport.None:
                case UtmValues.UsbBusSupport.Default:
                default:
                    break;
            }
        }

        _arguments.Add("-device");
        _arguments.Add("usb-tablet");

        // Host-driven keyboard: QMP send-key and SPICE client input deliver
        // through the input layer to virtio-keyboard; on fully headless
        // setups (no display backend) the emulated PS/2 route is unreliable.
        _arguments.Add("-device");
        _arguments.Add("virtio-keyboard-pci");

        if (input.HasUsbSharing)
        {
            for (var i = 0; i < Math.Clamp(input.MaximumUsbShare, 0, 16); i++)
            {
                var chardev = $"zutm-usbredir-{i}";
                _arguments.Add("-chardev");
                _arguments.Add($"spicevmc,id={chardev},name=usbredir");
                _arguments.Add("-device");
                _arguments.Add($"usb-redir,chardev={chardev}");
            }
        }
    }

    private void AddSerialArguments()
    {
        for (var i = 0; i < _configuration.Serials.Count; i++)
        {
            var serial = _configuration.Serials[i];
            if (serial.Target == UtmValues.SerialTarget.Gdb)
            {
                _arguments.Add("-gdb");
                _arguments.Add($"tcp:127.0.0.1:{_ports.QmpPort + 1 + i}");
                continue;
            }

            if (serial.Target == UtmValues.SerialTarget.Monitor)
            {
                _arguments.Add("-monitor");
                _arguments.Add($"tcp:127.0.0.1:{GetSerialPort(i)},server=on,wait=off");
                continue;
            }

            var wait = serial.IsWaitForConnection ? "wait=on" : "wait=off";
            _arguments.Add("-serial");
            _arguments.Add(serial.Mode switch
            {
                UtmValues.SerialMode.TcpClient => $"tcp:{serial.TcpHostAddress}:{serial.TcpPort}",
                UtmValues.SerialMode.TcpServer => $"tcp:127.0.0.1:{serial.TcpPort},server=on,{wait}",
                _ => $"tcp:127.0.0.1:{GetSerialPort(i)},server=on,{wait}",
            });
        }
    }

    private int GetSerialPort(int index) =>
        _ports.SerialPorts.TryGetValue(index, out var port) ? port : 4100 + index;

    private void AddDeviceArguments()
    {
        if (_configuration.Qemu.HasRngDevice)
        {
            _arguments.Add("-device");
            _arguments.Add("virtio-rng-pci");
        }

        if (_configuration.Qemu.HasBalloonDevice)
        {
            _arguments.Add("-device");
            _arguments.Add("virtio-balloon-pci");
        }

        if (_configuration.Qemu.HasTpmDevice)
        {
            _warnings.Add("TPM emulation requires swtpm, which is not bundled on Windows yet; the TPM device was skipped.");
        }
    }

    private void AddSharingArguments()
    {
        var sharing = _configuration.Sharing;
        if (sharing.DirectoryShareMode == UtmValues.DirectoryShareMode.WebDav)
        {
            // SPICE WebDAV channel: served by the spice-webdavd daemon inside the guest.
            _arguments.Add("-chardev");
            _arguments.Add("spiceport,name=org.spice-space.webdav.0,id=zutm-webdav");
            _arguments.Add("-device");
            _arguments.Add("virtserialport,chardev=zutm-webdav,name=org.spice-space.webdav.0");
        }
        else if (sharing.DirectoryShareMode == UtmValues.DirectoryShareMode.VirtFs)
        {
            _warnings.Add("VirtFS (9p) directory sharing has limited support in Windows QEMU builds; it may not work in the guest.");
        }

        if (sharing.HasClipboardSharing && _configuration.Displays.Count == 0)
        {
            _warnings.Add("Clipboard sharing requires a SPICE display; this VM is headless.");
        }
    }

    private void AddControlArguments()
    {
        _arguments.Add("-qmp");
        _arguments.Add($"tcp:127.0.0.1:{_ports.QmpPort},server=on,wait=off");

        if (_ports.GuestAgentPort > 0)
        {
            _arguments.Add("-chardev");
            _arguments.Add($"socket,id=zutm-qga,host=127.0.0.1,port={_ports.GuestAgentPort},server=on,wait=off");
            _arguments.Add("-device");
            _arguments.Add("virtserialport,chardev=zutm-qga,name=org.qemu.guest_agent.0");
        }
    }

    private void AddAdditionalArguments()
    {
        foreach (var argument in _configuration.Qemu.AdditionalArguments)
        {
            if (argument.Tokens is null)
            {
                _warnings.Add("An AdditionalArguments entry has an unrecognized shape and was not passed to QEMU; it is preserved in the config.");
                continue;
            }

            foreach (var token in argument.Tokens)
            {
                _arguments.Add(token);
            }
        }
    }
}
