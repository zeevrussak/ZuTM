// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
//
// End-to-end: boot the prepared Alpine headless image through ZuTM's own
// QEMU pipeline and communicate with the guest over its serial console —
// login, run commands, read results, and shut it down cleanly.

using System.Net.Sockets;
using ZuTM.Core.Qemu;
using ZuTM.Core.Utm;
using Xunit;

namespace ZuTM.E2E;

/// <summary>Shared plumbing for the Alpine VM suites.</summary>
public abstract class AlpineVmTestBase : IDisposable
{
    protected readonly string TempDir = Directory.CreateTempSubdirectory("zutm-e2e-vm-").FullName;

    protected static string TestImagesDir => Path.GetFullPath(
        Environment.GetEnvironmentVariable("ZUTM_TESTENV_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "testimages"));

    protected static QemuRuntime? FindQemu() => QemuRuntime.Discover();

    protected static bool ImageReady(string name) => File.Exists(Path.Combine(TestImagesDir, name));

    /// <summary>Creates a throwaway overlay so tests never mutate the golden image.</summary>
    protected string MakeOverlay(string backingImage)
    {
        var runtime = FindQemu() ?? throw new InvalidOperationException("QEMU runtime missing");
        var overlay = Path.Combine(TempDir, Path.GetFileName(backingImage));
        var qemuImg = Path.Combine(runtime.BinDirectory, "qemu-img.exe");
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            qemuImg, $"create -f qcow2 -b \"{backingImage}\" -F qcow2 \"{overlay}\"")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        Assert.Equal(0, process.ExitCode);
        return overlay;
    }

    protected static async Task<(QemuVmProcess Vm, SerialConsoleSession Console, QmpClient Qmp, QemuPortSet Ports)> LaunchAlpineAsync(
        string diskPath,
        int memoryMib,
        bool withDisplay,
        bool guestAgent = false)
    {
        var runtime = FindQemu() ?? throw new InvalidOperationException("QEMU runtime missing");
        var config = new UtmConfiguration
        {
            Information = new UtmInformation { Name = "zutm-e2e" },
            System = new UtmSystem { Architecture = "x86_64", Target = "q35", MemorySizeMib = memoryMib, CpuCount = 2 },
            Drives = [new UtmDrive { ImageName = "disk", ImageType = UtmValues.DriveImageType.Disk, Interface = UtmValues.DriveInterface.Virtio }],
            Serials = [new UtmSerial { Mode = UtmValues.SerialMode.Terminal, Target = UtmValues.SerialTarget.Auto }],
            Networks = [new UtmNetwork { Mode = UtmValues.NetworkMode.Shared, Hardware = "virtio-net-pci" }],
        };
        if (withDisplay)
        {
            config = config with { Displays = [new UtmDisplay { Hardware = "virtio-gpu-pci" }] };
        }

        var ports = PortAllocator.AllocateForLaunch(serialCount: 1, guestAgent: guestAgent);
        var plan = new QemuCommandLineBuilder(config, runtime, ports, drive => drive.ImageName == "disk" ? diskPath : null).Build();
        var vm = await QemuVmProcess.StartAsync(plan);

        var port = ports.SerialPorts[0];
        SerialConsoleSession? session = null;
        for (var attempt = 0; attempt < 40 && session is null; attempt++)
        {
            try
            {
                session = await SerialConsoleSession.ConnectAsync("127.0.0.1", port);
            }
            catch (SocketException)
            {
                await Task.Delay(250);
            }
        }

        Assert.NotNull(session);
        return (vm, session!, vm.Qmp!, ports);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(TempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class AlpineHeadlessVmTests : AlpineVmTestBase
{
    private const string ImageName = "alpine-headless.qcow2";

    [E2EFact]
    public async Task Boots_LogsIn_RunsCommands_AndShutsDownCleanly()
    {
        if (FindQemu() is null || !ImageReady(ImageName))
        {
            return; // environment not built — see scripts/testenv/build-images.ps1
        }

        var overlay = MakeOverlay(Path.Combine(TestImagesDir, ImageName));
        var (vm, console, qmp, _) = await LaunchAlpineAsync(overlay, memoryMib: 512, withDisplay: false);
        await using (console)
        await using (vm.Lease())
        {
            // Guest boots the installed Alpine; serial getty shows up.
            await console.LoginAsync("root", "zutm", TimeSpan.FromMinutes(3));

            // The VM is alive under QMP with hardware-accelerated-or-TCG config.
            Assert.Equal("running", await qmp.QueryStatusAsync());

            // Communicate: run a command and read its output back over serial.
            var uname = await console.RunAsync("uname -sr", "E2E-UNAME", TimeSpan.FromSeconds(30));
            Assert.Contains("Linux", uname);

            var arithmetic = await console.RunAsync("echo $((6*7))", "E2E-MATH", TimeSpan.FromSeconds(30));
            Assert.Contains("42", arithmetic);

            // Packages the image was built with are really installed.
            var agent = await console.RunAsync("apk info -e qemu-guest-agent", "E2E-GA", TimeSpan.FromSeconds(30));
            Assert.Contains("qemu-guest-agent", agent);

            // Clean ACPI-free poweroff via console; QEMU exits orderly.
            await console.SendLineAsync("poweroff");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            while (vm.State != VmRunState.Stopped)
            {
                await Task.Delay(250, cts.Token);
            }
        }
    }

    [Fact]
    public void PpmImage_ParsesQmpScreendumpFormat()
    {
        // Header with comment + binary raster: 2×1 px, red and black.
        byte[] ppm = [.. "P6\n# comment\n2 1\n255\n"u8, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00];
        var path = Path.Combine(TempDir, "probe.ppm");
        File.WriteAllBytes(path, ppm);

        var image = PpmImage.Parse(path);

        Assert.Equal(2, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal(38, image.MeanBrightness(), 1); // red ≈ (255·299)/1000
        Assert.Equal(1, image.CountBrightPixels());
    }
}
