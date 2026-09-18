// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Core.Qemu;
using ZuTM.Core.Utm;
using Xunit;

namespace ZuTM.Core.Tests.Qemu;

public class QemuCommandLineBuilderTests : IDisposable
{
    private readonly string _qemuRoot = Directory.CreateTempSubdirectory("zutm-qemu-root-").FullName;

    private QemuRuntime CreateRuntime()
    {
        Directory.CreateDirectory(Path.Combine(_qemuRoot, "bin"));
        Directory.CreateDirectory(Path.Combine(_qemuRoot, "share"));
        return new QemuRuntime(_qemuRoot);
    }

    private static UtmConfiguration NewConfig(Func<UtmConfiguration, UtmConfiguration>? customize = null)
    {
        var config = new UtmConfiguration
        {
            Information = new UtmInformation { Name = "My VM", Uuid = new Guid("6f0f0c6a-8dc0-47d1-a381-c0abf67a8c8d") },
            System = new UtmSystem { Architecture = "x86_64", Target = "q35", MemorySizeMib = 2048 },
            Displays = [new UtmDisplay { Hardware = "virtio-gpu-gl" }],
        };
        return customize is null ? config : customize(config);
    }

    private static QemuPortSet Ports(int spice = 5900, int qmp = 4444) => new()
    {
        SpicePort = spice,
        QmpPort = qmp,
        GuestAgentPort = 4455,
        SerialPorts = new Dictionary<int, int> { [0] = 4100 },
    };

    private QemuLaunchPlan Build(
        UtmConfiguration config,
        Func<UtmDrive, string?>? resolver = null,
        QemuAcceleration acceleration = QemuAcceleration.Tcg,
        string? efiVars = null) =>
        new QemuCommandLineBuilder(config, CreateRuntime(), Ports(), resolver, acceleration, efiVars).Build();

    private static int IndexOf(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == flag)
            {
                return i;
            }
        }

        return -1;
    }

    private static void AssertArg(IReadOnlyList<string> args, string flag, string value)
    {
        var index = IndexOf(args, flag);
        Assert.True(index >= 0, $"missing '{flag}' in: {string.Join(" ", args)}");
        Assert.True(index + 1 < args.Count, $"'{flag}' has no value");
        Assert.Equal(value, args[index + 1]);
    }

    private void AssertHasArg(string? assertMessage, IReadOnlyList<string> args, Func<string, bool> predicate) =>
        Assert.True(args.Any(predicate), $"{assertMessage}; arguments: {string.Join(" ", args)}");

    [Fact]
    public void Emits_MachineCpuMemory_NameUuid()
    {
        var plan = Build(NewConfig());

        AssertArg(plan.Arguments, "-name", "My VM");
        AssertArg(plan.Arguments, "-uuid", "6f0f0c6a-8dc0-47d1-a381-c0abf67a8c8d");
        AssertArg(plan.Arguments, "-machine", "q35");
        AssertArg(plan.Arguments, "-m", "2048");
        Assert.EndsWith("qemu-system-x86_64.exe", plan.ExecutablePath);
    }

    [Fact]
    public void UsesWhpx_WhenAcceleratorAvailable()
    {
        var plan = Build(NewConfig(), acceleration: QemuAcceleration.Whpx);
        AssertArg(plan.Arguments, "-accel", "whpx");
    }

    [Fact]
    public void UsesTcgSingleThread_ForOneVcpu()
    {
        var plan = Build(NewConfig(c => c = c with { System = c.System with { CpuCount = 1 } }));
        AssertArg(plan.Arguments, "-accel", "tcg,thread=single");
    }

    [Fact]
    public void UsesTcgMultiThread_ForManyVcpus_UnlessForcedSingle()
    {
        var plan = Build(NewConfig(c => c = c with { System = c.System with { CpuCount = 4 } }));
        AssertArg(plan.Arguments, "-accel", "tcg,thread=multi");

        var forced = Build(NewConfig(c => c = c with
        {
            System = c.System with { CpuCount = 4, ForceMulticore = true },
        }));
        AssertArg(forced.Arguments, "-accel", "tcg,thread=single");
    }

    [Fact]
    public void OmitCpu_WhenDefault_AndPassExplicitCpuWithFlags()
    {
        var plain = Build(NewConfig(c => c = c with { System = c.System with { Cpu = "default" } }));
        Assert.Equal(-1, IndexOf(plain.Arguments, "-cpu"));

        var flagged = Build(NewConfig(c => c = c with
        {
            System = c.System with
            {
                Cpu = "Skylake-Client",
                CpuFlagsAdd = ["sse4.2"],
                CpuFlagsRemove = ["pdpe1gb"],
            },
        }));
        AssertArg(flagged.Arguments, "-cpu", "Skylake-Client,+sse4.2,-pdpe1gb");
    }

    [Fact]
    public void FlagsWithoutExplicitCpu_ProduceWarning()
    {
        var plan = Build(NewConfig(c => c = c with
        {
            System = c.System with { Cpu = "default", CpuFlagsAdd = ["sse4.2"] },
        }));

        Assert.True(plan.Warnings.Any(w => w.Contains("CPU model")), "expected CPU model warning");
    }

    [Fact]
    public void SmpDefaultsToHostCount_WhenZero()
    {
        var plan = Build(NewConfig(c => c = c with { System = c.System with { CpuCount = 0 } }));
        AssertArg(plan.Arguments, "-smp", Environment.ProcessorCount.ToString());
    }

    [Fact]
    public void VirtioDisk_GetsDriveAndDevice()
    {
        var config = NewConfig(c => c = c with
        {
            Drives =
            [
                new UtmDrive
                {
                    ImageName = "disk-0.qcow2",
                    ImageType = UtmValues.DriveImageType.Disk,
                    Interface = UtmValues.DriveInterface.Virtio,
                },
            ],
        });

        var plan = Build(config, _ => @"C:\vm\Data\disk-0.qcow2");

        AssertHasArg("virtio drive", plan.Arguments, a => a == "id=zutm-drive-0,if=none,format=qcow2,file=C:\\vm\\Data\\disk-0.qcow2");
        AssertHasArg("virtio device", plan.Arguments, a => a == "virtio-blk-pci,drive=zutm-drive-0");
    }

    [Fact]
    public void DiskInterface_MapsToBuses()
    {
        var interfaces = new[]
        {
            (UtmValues.DriveInterface.Ide, "if=ide"),
            (UtmValues.DriveInterface.Scsi, "if=scsi"),
            (UtmValues.DriveInterface.Nvme, "if=none"),
            (UtmValues.DriveInterface.Usb, "if=none"),
        };

        foreach (var (iface, expected) in interfaces)
        {
            var config = NewConfig(c => c = c with
            {
                Drives = [new UtmDrive { ImageName = "d.img", ImageType = UtmValues.DriveImageType.Disk, Interface = iface }],
            });

            var plan = Build(config, _ => @"C:\vm\d.img");

            AssertHasArg($"interface {iface}", plan.Arguments,
                a => a.StartsWith("id=zutm-drive-0,", StringComparison.Ordinal) && a.Contains(expected, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ReadOnlyDisk_AddsReadOnlyFlag()
    {
        var config = NewConfig(c => c = c with
        {
            Drives = [new UtmDrive { ImageName = "d.qcow2", ImageType = UtmValues.DriveImageType.Disk, Interface = UtmValues.DriveInterface.Ide, IsReadOnly = true }],
        });

        var plan = Build(config, _ => @"C:\vm\d.qcow2");

        AssertHasArg("readonly", plan.Arguments, a => a.Contains("readonly=on", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingImage_Warns()
    {
        var config = NewConfig(c => c = c with
        {
            Drives = [new UtmDrive { ImageName = "gone.qcow2", ImageType = UtmValues.DriveImageType.Disk, Interface = UtmValues.DriveInterface.Ide }],
        });

        var plan = Build(config);

        Assert.True(plan.Warnings.Any(w => w.Contains("no resolvable image", StringComparison.Ordinal)), "expected missing-image warning");
    }

    [Fact]
    public void CdDrive_Empty_StillEmitted()
    {
        var config = NewConfig(c => c = c with
        {
            Drives = [new UtmDrive { ImageType = UtmValues.DriveImageType.Cd, Interface = UtmValues.DriveInterface.Ide }],
        });

        var plan = Build(config);

        AssertHasArg("empty cdrom", plan.Arguments, a => a == "id=zutm-cd-0,if=ide,media=cdrom");
    }

    [Fact]
    public void KernelInitrdDtb_MappedToBootArguments()
    {
        var config = NewConfig(c => c = c with
        {
            Drives =
            [
                new UtmDrive { ImageName = "vmlinuz", ImageType = UtmValues.DriveImageType.LinuxKernel },
                new UtmDrive { ImageName = "initrd.img", ImageType = UtmValues.DriveImageType.LinuxInitrd },
                new UtmDrive { ImageName = "board.dtb", ImageType = UtmValues.DriveImageType.LinuxDtb },
            ],
        });

        var plan = Build(config, drive => $@"C:\vm\{drive.ImageName}");

        AssertArg(plan.Arguments, "-kernel", @"C:\vm\vmlinuz");
        AssertArg(plan.Arguments, "-initrd", @"C:\vm\initrd.img");
        AssertArg(plan.Arguments, "-dtb", @"C:\vm\board.dtb");
    }

    [Fact]
    public void SharedNetwork_EmitsUserNetdevWithHostForwards()
    {
        var config = NewConfig(c => c = c with
        {
            Networks =
            [
                new UtmNetwork
                {
                    Mode = UtmValues.NetworkMode.Shared,
                    Hardware = "virtio-net-pci",
                    MacAddress = "52:54:00:12:34:56",
                    PortForward = [new UtmPortForward { Protocol = "tcp", HostPort = 2222, GuestPort = 22 }],
                },
            ],
        });

        var plan = Build(config);

        AssertHasArg("hostfwd netdev", plan.Arguments, a => a == "user,id=zutm-net-0,hostfwd=tcp::2222:22");
        AssertHasArg("nic", plan.Arguments, a => a == "virtio-net-pci,netdev=zutm-net-0,mac=52:54:00:12:34:56");
    }

    [Fact]
    public void IsolatedNetwork_AddsRestrict()
    {
        var config = NewConfig(c => c = c with
        {
            Networks = [new UtmNetwork { Mode = UtmValues.NetworkMode.Host, Hardware = "e1000", IsIsolateFromHost = true }],
        });

        var plan = Build(config);

        AssertHasArg("restrict", plan.Arguments, a => a == "user,id=zutm-net-0,restrict=on");
    }

    [Fact]
    public void BridgedNetwork_WarnsAndFallsBack()
    {
        var config = NewConfig(c => c = c with
        {
            Networks = [new UtmNetwork { Mode = UtmValues.NetworkMode.Bridged, Hardware = "e1000", BridgeInterface = "Ethernet" }],
        });

        var plan = Build(config);

        Assert.True(plan.Warnings.Any(w => w.Contains("bridged", StringComparison.Ordinal)), "expected bridged warning");
        AssertHasArg("fallback netdev", plan.Arguments, a => a == "user,id=zutm-net-0");
    }

    [Fact]
    public void Display_EmitsSpiceServerOnLoopback()
    {
        var plan = Build(NewConfig());

        AssertHasArg("gpu", plan.Arguments, a => a == "virtio-gpu-gl");
        AssertHasArg("spice", plan.Arguments, a => a == "port=5900,addr=127.0.0.1,disable-ticketing=on,image-compression=off,playback-compression=off,streaming-video=off");
        AssertHasArg("virtio-serial", plan.Arguments, a => a == "virtio-serial-pci");
    }

    [Fact]
    public void Headless_EmitsDisplayNone()
    {
        var plan = Build(NewConfig(c => c = c with { Displays = [] }));

        AssertArg(plan.Arguments, "-display", "none");
        Assert.False(plan.Arguments.Any(a => a.StartsWith("port=", StringComparison.Ordinal) && a.Contains("addr=127.0.0.1", StringComparison.Ordinal)),
            "headless VM must not start a SPICE server");
    }

    [Fact]
    public void Input_EmitsUsbTablet_AndRedirections()
    {
        var sharing = NewConfig(c => c = c with
        {
            Input = c.Input with { HasUsbSharing = true, MaximumUsbShare = 2 },
        });

        var plan = Build(sharing);

        AssertHasArg("tablet", plan.Arguments, a => a == "usb-tablet");
        Assert.Equal(2, plan.Arguments.Count(a => a.StartsWith("usb-redir,chardev=zutm-usbredir-", StringComparison.Ordinal)));
    }

    [Fact]
    public void Usb3_EmitsXhci()
    {
        var plan = Build(NewConfig(c => c = c with
        {
            Input = c.Input with { UsbBusSupport = UtmValues.UsbBusSupport.Usb3 },
        }));

        AssertHasArg("xhci", plan.Arguments, a => a == "qemu-xhci");
    }

    [Fact]
    public void SerialTerminal_BecomesLoopbackTcp()
    {
        var config = NewConfig(c => c = c with
        {
            Displays = [],
            Serials = [new UtmSerial { Mode = UtmValues.SerialMode.Terminal, Target = UtmValues.SerialTarget.Auto }],
        });

        var plan = Build(config);

        AssertArg(plan.Arguments, "-serial", "tcp:127.0.0.1:4100,server=on,wait=off");
    }

    [Fact]
    public void SerialTcpServer_HonorsWaitForConnection()
    {
        var config = NewConfig(c => c = c with
        {
            Serials = [new UtmSerial { Mode = UtmValues.SerialMode.TcpServer, TcpPort = 4321, IsWaitForConnection = true }],
        });

        var plan = Build(config);

        AssertArg(plan.Arguments, "-serial", "tcp:127.0.0.1:4321,server=on,wait=on");
    }

    [Fact]
    public void GdbTarget_RoutesToGdbStub()
    {
        var config = NewConfig(c => c = c with
        {
            Serials = [new UtmSerial { Target = UtmValues.SerialTarget.Gdb }],
        });

        var plan = Build(config);

        AssertArg(plan.Arguments, "-gdb", "tcp:127.0.0.1:4445");
    }

    [Fact]
    public void Devices_RngAndBalloon_AddedWhenEnabled()
    {
        var plan = Build(NewConfig());
        Assert.Contains("virtio-rng-pci", plan.Arguments);
        Assert.Contains("virtio-balloon-pci", plan.Arguments);

        var minimal = Build(NewConfig(c => c = c with
        {
            Qemu = c.Qemu with { HasRngDevice = false, HasBalloonDevice = false },
        }));
        Assert.DoesNotContain("virtio-rng-pci", minimal.Arguments);
        Assert.DoesNotContain("virtio-balloon-pci", minimal.Arguments);
    }

    [Fact]
    public void TpmDevice_Warns_SkippedOnWindows()
    {
        var plan = Build(NewConfig(c => c = c with { Qemu = c.Qemu with { HasTpmDevice = true } }));

        Assert.True(plan.Warnings.Any(w => w.Contains("swtpm", StringComparison.Ordinal)), "expected swtpm warning");
        Assert.DoesNotContain("tpm-tis", plan.Arguments);
    }

    [Fact]
    public void UefiBoot_EmitsPflashDrives_WhenFirmwarePresent()
    {
        var runtime = CreateRuntime();
        var edk2 = Path.Combine(runtime.ShareDirectory, "edk2-x86_64");
        Directory.CreateDirectory(edk2);
        File.WriteAllText(Path.Combine(edk2, "code.fd"), "fw");

        var plan = new QemuCommandLineBuilder(
            NewConfig(c => c = c with { Qemu = c.Qemu with { HasUefiBoot = true } }),
            runtime,
            Ports(),
            acceleration: QemuAcceleration.Tcg,
            efiVarsPath: @"C:\vm\efi_vars.fd").Build();

        AssertHasArg("code pflash", plan.Arguments, a => a.Contains("if=pflash", StringComparison.Ordinal) && a.Contains("readonly=on", StringComparison.Ordinal));
        AssertHasArg("vars pflash", plan.Arguments, a => a == "if=pflash,format=raw,file=C:\\vm\\efi_vars.fd");
    }

    [Fact]
    public void UefiBoot_Warns_WhenFirmwareMissing()
    {
        var plan = Build(NewConfig(c => c = c with { Qemu = c.Qemu with { HasUefiBoot = true } }));

        Assert.True(plan.Warnings.Any(w => w.Contains("EDK2", StringComparison.Ordinal)), "expected EDK2 warning");
    }

    [Fact]
    public void WebDavSharing_EmitsSpiceChannel()
    {
        var plan = Build(NewConfig(c => c = c with
        {
            Sharing = c.Sharing with { DirectoryShareMode = UtmValues.DirectoryShareMode.WebDav },
        }));

        AssertHasArg("webdav channel", plan.Arguments, a => a == "spiceport,name=org.spice-space.webdav.0,id=zutm-webdav");
    }

    [Fact]
    public void RtcLocalTime_EmitsRtcBase()
    {
        var plan = Build(NewConfig(c => c = c with { Qemu = c.Qemu with { HasRtcLocalTime = true } }));
        AssertArg(plan.Arguments, "-rtc", "base=localtime");
    }

    [Fact]
    public void ControlPlane_EmitsQmpAndGuestAgent()
    {
        var plan = Build(NewConfig());

        AssertArg(plan.Arguments, "-qmp", "tcp:127.0.0.1:4444,server=on,wait=off");
        AssertHasArg("qga chardev", plan.Arguments, a => a == "socket,id=zutm-qga,host=127.0.0.1,port=4455,server=on,wait=off");
        AssertHasArg("qga port", plan.Arguments, a => a == "virtserialport,chardev=zutm-qga,name=org.qemu.guest_agent.0");
    }

    [Fact]
    public void MachinePropertyOverride_AppendedToMachine()
    {
        var plan = Build(NewConfig(c => c = c with
        {
            Qemu = c.Qemu with { MachinePropertyOverride = "hpet=off" },
        }));

        AssertArg(plan.Arguments, "-machine", "q35,hpet=off");
    }

    [Fact]
    public void AdditionalArguments_AppendedVerbatim()
    {
        var plan = Build(NewConfig(c => c = c with
        {
            Qemu = c.Qemu with { AdditionalArguments = [new UtmQemuArgument(["-boot", "menu=on"]), new UtmQemuArgument(["-global", "mcp55-fdc.fd=0"])] },
        }));

        var index = IndexOf(plan.Arguments, "-boot");
        Assert.True(index >= 0, "-boot missing");
        Assert.Equal("menu=on", plan.Arguments[index + 1]);
        Assert.Contains("-global", plan.Arguments);
    }

    [Fact]
    public void SoundDevices_Emitted()
    {
        var plan = Build(NewConfig(c => c = c with { Sounds = [new UtmSound { Hardware = "intel-hda" }] }));
        AssertContainsArgument(plan, "intel-hda");
    }

    private static void AssertContainsArgument(QemuLaunchPlan plan, string argument) =>
        Assert.Contains(argument, plan.Arguments);

    [Fact]
    public void Vdagent_EmittedWithDisplay_ForClipboardAndDynamicResolution()
    {
        var plan = Build(NewConfig());
        AssertContainsArgument(plan, "spicevmc,id=zutm-vdagent,name=vdagent");
        AssertContainsArgument(plan, "virtserialport,chardev=zutm-vdagent,name=com.redhat.spice.0");
    }

    [Fact]
    public void CommandLine_RendersQuotedExecutable()
    {
        var plan = Build(NewConfig());
        Assert.StartsWith("\"", plan.CommandLine, StringComparison.Ordinal);
        Assert.Contains("-machine q35", plan.CommandLine, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_qemuRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
