// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
//
// Builds the E2E testing environment by driving real virtual machines over
// their serial consoles — the same QEMU/serial/QMP plumbing ZuTM.App uses.
//
//   dotnet run --project tools/ZuTM.TestEnv -- iso        # fetch + verify Alpine ISO
//   dotnet run --project tools/ZuTM.TestEnv -- headless   # unattended Alpine install → alpine-headless.qcow2
//   dotnet run --project tools/ZuTM.TestEnv -- ui         # + XFCE + SPICE/agent tools → alpine-xfce.qcow2
//   dotnet run --project tools/ZuTM.TestEnv -- clean

using System.Diagnostics;
using System.Net.Sockets;
using System.Net.Http;
using System.Security.Cryptography;
using ZuTM.Core.Qemu;
using ZuTM.Core.Utm;

namespace ZuTM.TestEnv;

internal static class Program
{
    // Alpine "virt" flavor: small, VM-oriented, serial console on ttyS0.
    private const string AlpineSeries = "v3.22";

    private static readonly string WorkDir = Path.GetFullPath(
        Environment.GetEnvironmentVariable("ZUTM_TESTENV_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "testimages"));

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private const string RootPassword = "zutm";

    private static async Task<int> Main(string[] args)
    {
        var command = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "help";
        try
        {
            Directory.CreateDirectory(WorkDir);
            return command switch
            {
                "iso" => await DownloadIsoAsync(),
                "headless" => await InstallHeadlessAsync(),
                "ui" => await InstallUiAsync(),
                "probe" => await ProbeAsync(),
                "probe-ui" => await ProbeUiAsync(),
                "clean" => Clean(),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            ZuTM.TestEnv — builds the E2E VM testing environment.

            commands:
              iso        download + SHA-256-verify the Alpine virt ISO
              headless   unattended Alpine install (serial-driven) → alpine-headless.qcow2
              ui         XFCE + spice-vdagent + qemu-guest-agent on top → alpine-xfce.qcow2
              clean      remove testimages/

            options (env):
              ZUTM_TESTENV_DIR   output directory (default <repo>/testimages)
            """);
        return 0;
    }

    private static int Clean()
    {
        if (Directory.Exists(WorkDir))
        {
            Directory.Delete(WorkDir, recursive: true);
        }

        Console.WriteLine($"removed {WorkDir}");
        return 0;
    }

    // -- ISO ----------------------------------------------------------------------

    private static string IsoPath => Path.Combine(WorkDir, "alpine-virt.iso");

    private static async Task<int> DownloadIsoAsync()
    {
        var indexUrl = $"https://dl-cdn.alpinelinux.org/alpine/{AlpineSeries}/releases/x86_64/";
        using var http = new HttpClient();
        var html = await http.GetStringAsync(indexUrl);

        // Newest alpine-virt-<version>-x86_64.iso in the series.
        var match = System.Text.RegularExpressions.Regex
            .Matches(html, @"alpine-virt-(?<ver>\d+\.\d+\.\d+)-x86_64\.iso")
            .OrderByDescending(m => Version.Parse(m.Groups["ver"].Value))
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"no alpine-virt ISO found at {indexUrl}");

        var fileName = match.Value;
        if (File.Exists(IsoPath))
        {
            Console.WriteLine($"ISO already present: {IsoPath}");
            return 0;
        }

        Console.WriteLine($"downloading {indexUrl}{fileName} …");
        var target = Path.Combine(WorkDir, fileName);
        using (var response = await http.GetAsync(indexUrl + fileName, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(target);
            await response.Content.CopyToAsync(file);
        }

        // Integrity: verify the published per-asset .sha256 companion file.
        Console.WriteLine("verifying SHA-256 …");
        var checksum = await http.GetStringAsync($"{indexUrl}{fileName}.sha256");
        var expected = System.Text.RegularExpressions.Regex
            .Match(checksum, "[0-9a-f]{64}")
            .Value;
        if (expected.Length != 64)
        {
            throw new InvalidOperationException($"no checksum found for {fileName}");
        }

        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(File.OpenRead(target)));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            throw new InvalidOperationException($"checksum mismatch for {fileName}");
        }

        File.Copy(target, IsoPath, overwrite: true);
        try
        {
            File.Delete(target); // best-effort; a leftover temp copy is harmless
        }
        catch (IOException)
        {
        }
        Console.WriteLine($"ISO ready: {IsoPath} ({new FileInfo(IsoPath).Length / 1024 / 1024} MiB)");
        return 0;
    }

    // -- QEMU helpers ---------------------------------------------------------------

    private static QemuRuntime RequireQemu() =>
        QemuRuntime.Discover()
        ?? throw new InvalidOperationException(
            "QEMU runtime not found — run scripts/fetch-qemu.ps1 or set ZUTM_QEMU_ROOT.");

    private static void RunQemuImg(QemuRuntime runtime, string arguments)
    {
        var qemuImg = Path.Combine(runtime.BinDirectory, "qemu-img.exe");
        var process = Process.Start(new ProcessStartInfo(qemuImg, arguments)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("failed to start qemu-img");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"qemu-img {arguments} failed: {stderr.Trim()}");
        }
    }

    /// <summary>Extracts the virt kernel + initramfs from the ISO with 7-Zip (no ISO mounting needed).</summary>
    private static (string Kernel, string Initrd) ExtractBootFiles()
    {
        var sevenZip = FindSevenZip();
        var kernel = Path.Combine(WorkDir, "vmlinuz-virt");
        var initrd = Path.Combine(WorkDir, "initramfs-virt");
        if (File.Exists(kernel) && File.Exists(initrd))
        {
            return (kernel, initrd);
        }

        Console.WriteLine("extracting boot files from ISO (7-Zip) …");
        foreach ((var archivePath, var destination) in new[] { ("boot/vmlinuz-virt", kernel), ("boot/initramfs-virt", initrd) })
        {
            var process = Process.Start(new ProcessStartInfo(sevenZip, $"x -y -o\"{WorkDir}\" \"{IsoPath}\" {archivePath}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            })!;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit(120_000);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"7-Zip failed to extract {archivePath}");
            }

            var extracted = Path.Combine(WorkDir, archivePath.Replace('/', Path.DirectorySeparatorChar));
            File.Move(extracted, destination, overwrite: true);
        }

        return (kernel, initrd);
    }

    private static string FindSevenZip() =>
        new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
        }
        .FirstOrDefault(File.Exists)
        ?? throw new InvalidOperationException("7-Zip not found (needed to unpack the ISO).");

    /// <summary>The launch configuration shared by install and test runs.</summary>
    private static UtmConfiguration BaseConfiguration(int memoryMib, bool withDisplay)
    {
        var config = new UtmConfiguration
        {
            Information = new UtmInformation { Name = "zutm-testenv" },
            System = new UtmSystem
            {
                Architecture = "x86_64",
                Target = "q35",
                MemorySizeMib = memoryMib,
                CpuCount = 2,
            },
            Serials = [new UtmSerial { Mode = UtmValues.SerialMode.Terminal, Target = UtmValues.SerialTarget.Auto }],
            Networks = [new UtmNetwork { Mode = UtmValues.NetworkMode.Shared, Hardware = "virtio-net-pci" }],
        };

        if (withDisplay)
        {
            config = config with
            {
                Displays = [new UtmDisplay { Hardware = "virtio-gpu-pci" }],
                Sounds = [],
            };
        }

        return config;
    }

    private static async Task<(QemuVmProcess Vm, SerialConsoleSession Console, int SerialPort)> LaunchAsync(
        UtmConfiguration configuration,
        Func<UtmDrive, string?>? imageResolver = null,
        string[]? extraArguments = null,
        bool guestAgent = false)
    {
        var runtime = RequireQemu();
        var ports = PortAllocator.AllocateForLaunch(configuration.Serials.Count, guestAgent: guestAgent);
        if (extraArguments is not null)
        {
            configuration = configuration with
            {
                Qemu = configuration.Qemu with
                {
                    AdditionalArguments =
                    [
                        .. configuration.Qemu.AdditionalArguments,
                        new UtmQemuArgument(extraArguments),
                    ],
                },
            };
        }

        var plan = new QemuCommandLineBuilder(configuration, runtime, ports, imageResolver).Build();
        var vm = await QemuVmProcess.StartAsync(plan, logPath: Path.Combine(WorkDir, "testenv-qemu.log"));

        var serialPort = ports.SerialPorts[0];
        SerialConsoleSession? session = null;
        for (var attempt = 0; attempt < 40 && session is null; attempt++)
        {
            try
            {
                session = await SerialConsoleSession.ConnectAsync("127.0.0.1", serialPort);
            }
            catch (SocketException)
            {
                await Task.Delay(250);
            }
        }

        if (session is null)
        {
            await vm.DisposeAsync();
            throw new InvalidOperationException("serial console did not come up");
        }

        return (vm, session, serialPort);
    }

    private static async Task RunCommandsAsync(SerialConsoleSession console, (string Command, string Description)[] commands, TimeSpan perCommandTimeout)
    {
        for (var i = 0; i < commands.Length; i++)
        {
            var (command, description) = commands[i];
            var marker = $"ZUTMOK{i}";
            Console.Out.WriteLine($"  [{i + 1}/{commands.Length}] {description}");
            try
            {
                // Split-marker idiom: the echoed input contains ZUTM""OK, the
                // output ZUTMOK — completion can only be proven by real output.
                await console.SendLineAsync($"({command}) && echo ZUTM\"\"OK{i} || echo ZUTMFA\"\"IL{i}");
                await console.WaitForAsync(marker, perCommandTimeout);
                var tail = console.Log;
                Console.Out.WriteLine("    > " + tail[^Math.Min(700, tail.Length)..].Replace("\n", "\n    > "));
            }
            catch (OperationCanceledException)
            {
                var log = console.Log;
                var diagnostics = "";
                try
                {
                    var status = CurrentVm is { Qmp: { } qmp } ? await qmp.QueryStatusAsync() : "n/a";
                    diagnostics = $"\nQMP status: {status}";
                    diagnostics += $"\nQEMU log tail:\n{TailFile(Path.Combine(WorkDir, "testenv-qemu.log"), 1500)}";
                }
                catch (Exception)
                {
                    // Diagnostics are best-effort inside a failure path.
                }

                throw new InvalidOperationException(
                    $"timed out on: {description}{diagnostics}\n--- console tail ---\n{log[^Math.Min(3000, log.Length)..]}");
            }
        }
    }

    private static QemuVmProcess? CurrentVm;

    private static string TailFile(string path, int maxChars)
    {
        if (!File.Exists(path))
        {
            return "(no log)";
        }

        var text = File.ReadAllText(path);
        return text[^Math.Min(maxChars, text.Length)..];
    }

    private static async Task PowerOffAsync(QemuVmProcess vm, SerialConsoleSession console)
    {
        // `poweroff` tears the guest down before any echo could come back, so
        // send it raw and wait for the QEMU process to exit.
        await console.SendLineAsync("poweroff");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            while (vm.State != VmRunState.Stopped)
            {
                await Task.Delay(250, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            await vm.DisposeAsync(); // graceful window elapsed — force
        }
    }

    // -- headless install ------------------------------------------------------------

    private static async Task<int> InstallHeadlessAsync()
    {
        await DownloadIsoAsync();
        var runtime = RequireQemu();
        var (kernel, initrd) = ExtractBootFiles();

        var disk = Path.Combine(WorkDir, "alpine-headless.qcow2");
        if (File.Exists(disk))
        {
            Console.WriteLine("headless image already exists (delete it or run 'clean' to rebuild)");
            return 0;
        }

        Console.WriteLine("creating 2 GiB disk …");
        RunQemuImg(runtime, $"create -f qcow2 \"{disk}\" 2G");

        Console.WriteLine("booting Alpine installer (direct kernel boot, serial console) …");
        var config = BaseConfiguration(memoryMib: 512, withDisplay: false) with
        {
            Drives =
            [
                new UtmDrive { ImageName = "cdrom", ImageType = UtmValues.DriveImageType.Cd, Interface = UtmValues.DriveInterface.Ide },
                new UtmDrive { ImageName = "disk", ImageType = UtmValues.DriveImageType.Disk, Interface = UtmValues.DriveInterface.Virtio },
            ],
        };

        var resolver = new Dictionary<string, string>
        {
            ["cdrom"] = IsoPath,
            ["disk"] = disk,
        };

        var (vm, console, _) = await LaunchAsync(
            config,
            imageResolver: drive => drive.ImageName is not null && resolver.TryGetValue(drive.ImageName, out var path) ? path : null,
            extraArguments:
            [
                "-kernel", kernel,
                "-initrd", initrd,
                "-append", "console=ttyS0,115200 modules=loop,squashfs,sd-mod,usb-storage quiet",
            ]);
        CurrentVm = vm;

        await using (console)
        await using (vm.Lease())
        {
            Console.WriteLine("waiting for installer login prompt …");
            await console.LoginAsync("root", password: null, TimeSpan.FromMinutes(5));

            Console.WriteLine("installing Alpine to disk (unattended) …");
            await RunCommandsAsync(console,
            [
                ("echo root:" + RootPassword + " | chpasswd", "set root password (live)"),
                ("setup-interfaces -a && rc-service networking restart", "DHCP on eth0 (live)"),
                ("setup-apkrepos -1", "network repository (live)"),
                ("apk add -q syslinux", "syslinux bootloader package (missing from virt ISO)"),
                ("ERASE_DISKS=/dev/vda setup-disk -m sys -s 0 /dev/vda", "setup-disk: sys mode onto /dev/vda"),
                ("sed -i 's/^default_kernel_opts=.*/default_kernel_opts=\"console=ttyS0,115200\"/' /mnt/etc/update-extlinux.conf", "kernel opts: serial console"),
                ("echo 'ttyS0::respawn:/sbin/getty -L 115200 ttyS0 vt100' >> /mnt/etc/inittab", "serial getty on installed system"),
                ("echo root:" + RootPassword + " | chroot /mnt chpasswd", "set root password (installed)"),
                ("sync", "flush to disk"),
            ],
            perCommandTimeout: TimeSpan.FromMinutes(10));

            Console.WriteLine("powering off …");
            await PowerOffAsync(vm, console);
        }

        Console.WriteLine($"headless image ready: {disk}");
        return 0;
    }

    // -- diagnostics -------------------------------------------------------------------

    /// <summary>Boots the installed disk with the ISO kernel and dumps partition/extlinux state from the initramfs shell.</summary>
    private static async Task<int> ProbeAsync()
    {
        await DownloadIsoAsync();
        var runtime = RequireQemu();
        var (kernel, initrd) = ExtractBootFiles();
        var disk = Path.Combine(WorkDir, "alpine-headless.qcow2");
        if (!File.Exists(disk))
        {
            throw new InvalidOperationException("headless image missing");
        }

        var overlay = Path.Combine(WorkDir, "probe.qcow2");
        File.Delete(overlay);
        RunQemuImg(runtime, $"create -f qcow2 -b \"{disk}\" -F qcow2 \"{overlay}\" 2G");

        var config = BaseConfiguration(memoryMib: 512, withDisplay: false) with
        {
            Drives = [new UtmDrive { ImageName = "disk", ImageType = UtmValues.DriveImageType.Disk, Interface = UtmValues.DriveInterface.Virtio }],
        };
        var (vm, console, _) = await LaunchAsync(
            config,
            imageResolver: drive => drive.ImageName == "disk" ? overlay : null,
            extraArguments: ["-kernel", kernel, "-initrd", initrd, "-append", "console=ttyS0,115200"]);
        CurrentVm = vm;

        await using (console)
        await using (vm.Lease())
        {
            await console.WaitForAsync("# ", TimeSpan.FromMinutes(3));
            await console.SendLineAsync("cat /proc/partitions; blkid");
            await Task.Delay(2000);
            await console.SendLineAsync("fdisk -l /dev/vda 2>&1; echo ===FDISK===");
            await console.WaitForAsync("===FDISK===", TimeSpan.FromSeconds(20));
            await console.SendLineAsync("mkdir -p /r; mount /dev/vda3 /r 2>/dev/null || mount /dev/vda2 /r 2>/dev/null || mount /dev/vda1 /r 2>/dev/null || mount /dev/vda /r 2>/dev/null; ls /r; echo ===MOUNT===");
            await console.WaitForAsync("===MOUNT===", TimeSpan.FromSeconds(20));
            await console.SendLineAsync("cat /r/boot/extlinux.conf; echo ===EXTLINUX===");
            await console.WaitForAsync("===EXTLINUX===", TimeSpan.FromSeconds(20));
            await console.SendLineAsync("echo ===PROBEDONE===");
            await console.WaitForAsync("===PROBEDONE===", TimeSpan.FromSeconds(30));
            Console.WriteLine("---- serial diagnostic log ----");
            Console.WriteLine(console.Log);
        }

        return 0;
    }

    /// <summary>Boots the XFCE image and dumps service/display state over serial for diagnosis.</summary>
    private static async Task<int> ProbeUiAsync()
    {
        var runtime = RequireQemu();
        var disk = Path.Combine(WorkDir, "alpine-xfce.qcow2");
        if (!File.Exists(disk))
        {
            throw new InvalidOperationException("UI image missing");
        }

        var overlay = Path.Combine(WorkDir, "probeui.qcow2");
        File.Delete(overlay);
        RunQemuImg(runtime, $"create -f qcow2 -b \"{disk}\" -F qcow2 \"{overlay}\"");

        var config = BaseConfiguration(memoryMib: 1024, withDisplay: true) with
        {
            Drives = [new UtmDrive { ImageName = "disk", ImageType = UtmValues.DriveImageType.Disk, Interface = UtmValues.DriveInterface.Virtio }],
        };
        config = config with { Displays = [new UtmDisplay { Hardware = "qxl-vga" }] };
        var (vm, console, _) = await LaunchAsync(
            config,
            imageResolver: drive => drive.ImageName == "disk" ? overlay : null,
            guestAgent: true);

        await using (console)
        await using (vm.Lease())
        {
            await Task.Delay(TimeSpan.FromSeconds(90)); // give lightdm a chance under TCG
            await console.LoginAsync("root", RootPassword, TimeSpan.FromMinutes(3));
            // Capture kernel-level keyboard events while the host types.
            // Capture kernel-level keyboard events while the host types.
            await console.RunAsync("kbd=$(grep -l -m1 -E 'AT Translated|virtio.*eyboard' /sys/class/input/event*/device/name 2>/dev/null | head -1 | grep -o 'event[0-9]*'); echo KBDDEV=$kbd; (timeout 12 od -c /dev/input/$kbd > /tmp/ev.txt 2>&1 &) ; echo ===KBDDEV===", "===KBDDEV===", TimeSpan.FromSeconds(30));

            // Host-side typing into whatever has focus (the autostarted terminal).
            var qmp = vm.Qmp!;
            // HMP sendkey — often routes where headless QMP send-key does not.
            await console.RunAsync("(timeout 10 od -c /dev/input/event0 > /tmp/evhmp.txt 2>&1 &); echo ===HMPWIN===", "===HMPWIN===", TimeSpan.FromSeconds(30));
            foreach (var key in new[] { "t", "o", "u", "c", "h" })
            {
                await qmp.ExecuteAsync("human-monitor-command", new Dictionary<string, object?> { ["command-line"] = $"sendkey {key}" });
                await Task.Delay(300);
            }
            await Task.Delay(TimeSpan.FromSeconds(10));
            await console.RunAsync("cat /tmp/evhmp.txt | head -12; echo ===EVDUMPHMP===", "===EVDUMPHMP===", TimeSpan.FromSeconds(30));
            foreach (var key in new[] { "t", "o", "u", "c", "h", "spc", "slash", "t", "m", "p", "slash", "p", "r", "o", "b", "e", "ret" })
            {
                await qmp.ExecuteAsync("send-key", new { keys = new object[] { new { type = "qcode", data = key } } });
                await Task.Delay(150);
            }
            await Task.Delay(TimeSpan.FromSeconds(15));

            await console.RunAsync("cat /tmp/ev.txt | head -24; echo ===EVDUMP===", "===EVDUMP===", TimeSpan.FromSeconds(30));
            await console.RunAsync("rc-status default; echo ===RC===", "===RC===", TimeSpan.FromSeconds(30));
            await console.RunAsync("rc-service lightdm status; echo ===LDM===", "===LDM===", TimeSpan.FromSeconds(30));
            await console.RunAsync("tail -30 /var/log/lightdm/lightdm.log 2>/dev/null; echo ===LOG===", "===LOG===", TimeSpan.FromSeconds(30));
            await console.RunAsync("cat /etc/lightdm/lightdm.conf.d/90-zutm.conf; echo ===CONF===", "===CONF===", TimeSpan.FromSeconds(30));
            await console.RunAsync("ls /usr/share/xsessions/; echo ===XS===; tail -20 /var/log/Xorg.0.log 2>/dev/null; echo ===X===", "===X===", TimeSpan.FromSeconds(30));
            await console.RunAsync("cat /home/tester/.xsession-errors 2>/dev/null | tail -40; echo ===XERR===", "===XERR===", TimeSpan.FromSeconds(30));
            await console.RunAsync("ps aux | grep -E 'xfce|Xorg' | grep -v grep; echo ===PS===", "===PS===", TimeSpan.FromSeconds(30));
            await console.RunAsync("tail -15 /var/log/Xorg.0.log 2>/dev/null; echo ===XLOG===", "===XLOG===", TimeSpan.FromSeconds(30));
            await console.RunAsync("tail -15 /home/tester/.xsession-errors 2>/dev/null; echo ===XERR2===", "===XERR2===", TimeSpan.FromSeconds(30));
            await console.RunAsync("DISPLAY=:0 XAUTHORITY=/home/tester/.Xauthority xdpyinfo 2>&1 | head -6; echo ===DPY===", "===DPY===", TimeSpan.FromSeconds(30));
            await console.RunAsync("pid=$(pgrep -u tester xfce4-session | head -1); xargs -0 -n1 < /proc/$pid/environ | grep -E 'DISPLAY|XAUTHORITY|DBUS'; echo ===ENVR===; ls -la /home/tester/.Xauthority; echo ===XAUTHLS===", "===XAUTHLS===", TimeSpan.FromSeconds(30));
            await console.RunAsync("su - tester -c 'DISPLAY=:0 XAUTHORITY=/home/tester/.Xauthority xdotool getdisplaygeometry; echo rc=$?' ; echo ===SU1===", "===SU1===", TimeSpan.FromSeconds(30));
            await console.RunAsync("su - tester -c 'DISPLAY=:0 XAUTHORITY=/home/tester/.Xauthority xdotool getdisplaygeometry; echo rc=$?' ; echo ===SU2===", "===SU2===", TimeSpan.FromSeconds(30));
                        await console.RunAsync("cat /proc/bus/input/devices | grep -i -A1 name | head -12; echo ===INPUTS===", "===INPUTS===", TimeSpan.FromSeconds(30));
            await console.RunAsync("su - tester -c 'DISPLAY=:0 XAUTHORITY=/home/tester/.Xauthority xdotool getactivewindow getwindowname; echo rc=$?'; ls -l /tmp/probe /tmp/PROBE 2>&1; echo ===ACTIVE===", "===ACTIVE===", TimeSpan.FromSeconds(30));
            await console.RunAsync("grep -iE 'input|kbd' /var/log/Xorg.0.log | head -12; echo ===XINPUT===", "===XINPUT===", TimeSpan.FromSeconds(30));
            Console.WriteLine("---- probe-ui log ----");
            Console.WriteLine(console.Log);
        }

        return 0;
    }

    // -- UI install --------------------------------------------------------------------

    private static async Task<int> InstallUiAsync()
    {
        var runtime = RequireQemu();
        var headless = Path.Combine(WorkDir, "alpine-headless.qcow2");
        if (!File.Exists(headless))
        {
            await InstallHeadlessAsync();
        }

        var ui = Path.Combine(WorkDir, "alpine-xfce.qcow2");
        if (File.Exists(ui))
        {
            Console.WriteLine("UI image already exists (delete it or run 'clean' to rebuild)");
            return 0;
        }

        Console.WriteLine("creating UI overlay on headless base …");
        RunQemuImg(runtime, $"create -f qcow2 -b \"{headless}\" -F qcow2 \"{ui}\" 8G");

        Console.WriteLine("booting installed Alpine (serial-driven) …");
        var config = BaseConfiguration(memoryMib: 1024, withDisplay: true) with
        {
            Drives = [new UtmDrive { ImageName = "disk", ImageType = UtmValues.DriveImageType.Disk, Interface = UtmValues.DriveInterface.Virtio }],
        };
        var (vm, console, _) = await LaunchAsync(
            config,
            imageResolver: drive => drive.ImageName == "disk" ? ui : null,
            guestAgent: true);
        CurrentVm = vm;

        await using (console)
        await using (vm.Lease())
        {
            Console.WriteLine("waiting for installed system login …");
            await console.LoginAsync("root", RootPassword, TimeSpan.FromMinutes(5));

            Console.WriteLine("installing XFCE + SPICE guest tools (network install, be patient) …");
            await RunCommandsAsync(console,
            [
                ("printf 'auto lo\\niface lo inet loopback\\n\\nauto eth0\\niface eth0 inet dhcp\\n' > /etc/network/interfaces && rc-service networking restart", "DHCP on eth0"),
                ("setup-apkrepos -1", "configure network package repository (main)"),
                ("echo http://dl-cdn.alpinelinux.org/alpine/v3.22/community >> /etc/apk/repositories && apk update && apk search -x xfce4 >/dev/null", "enable community repository (XFCE lives there)"),
                ("apk update", "apk update"),
                ("apk add --no-progress xorg-server xfce4 xfce4-terminal lightdm lightdm-gtk-greeter spice-vdagent qemu-guest-agent imagemagick xdotool", "install XFCE + spice-vdagent + qemu-guest-agent"),
                ("rc-update add lightdm default", "enable lightdm"),
                ("rc-update add spice-vdagentd default", "enable spice-vdagent"),
                ("rc-update add qemu-guest-agent default", "enable qemu-guest-agent"),
                ("adduser -D tester", "create autologin user"),
                ("mkdir -p /etc/lightdm/lightdm.conf.d", "conf.d dir"),
                ("mkdir -p /home/tester/.config/autostart && echo '[Desktop Entry]' > /home/tester/.config/autostart/zutm-terminal.desktop && echo 'Type=Application' >> /home/tester/.config/autostart/zutm-terminal.desktop && echo 'Name=ZuTM Test Terminal' >> /home/tester/.config/autostart/zutm-terminal.desktop && echo 'Exec=xfce4-terminal' >> /home/tester/.config/autostart/zutm-terminal.desktop && echo 'Terminal=false' >> /home/tester/.config/autostart/zutm-terminal.desktop", "autostart xfce4-terminal (E2E keyboard input sink)"),
                ("printf '[Seat:*]\\nautologin-user=tester\\nautologin-session=xfce\\nautologin-user-timeout=0\\n' > /etc/lightdm/lightdm.conf.d/90-zutm.conf", "lightdm autologin to XFCE"),
                ("mkdir -p /home/tester/.config/xfce4/xfconf/xfce-perchannel-xml && printf '%s\\n' '<channel name=\"xfce4-keyboard-shortcuts\" version=\"1.0\">' '  <property name=\"commands\" type=\"empty\">' '    <property name=\"custom\" type=\"empty\">' '      <property name=\"&lt;Primary&gt;&lt;Alt&gt;t\" type=\"string\" value=\"xfce4-terminal\"/>' '    </property>' '  </property>' '</channel>' > /home/tester/.config/xfce4/xfconf/xfce-perchannel-xml/xfce4-keyboard-shortcuts.xml && chown -R tester:tester /home/tester/.config", "bind Ctrl+Alt+T to xfce4-terminal"),
                ("sed -i 's/^#\\?greeter-session=.*/greeter-session=lightdm-gtk-greeter/' /etc/lightdm/lightdm.conf || true", "greeter session"),
                ("rc-update add networking boot", "networking at boot"),
                ("sync", "flush"),
            ],
            perCommandTimeout: TimeSpan.FromMinutes(20));

            Console.WriteLine("powering off …");
            await PowerOffAsync(vm, console);
        }

        Console.WriteLine($"UI image ready: {ui}");
        return 0;
    }
}
