// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Diagnostics;
using System.Net.Sockets;
using ZuTM.Core.Qemu;
using ZuTM.Core.Utm;
using Xunit;

namespace ZuTM.E2E;

/// <summary>
/// Full lifecycle against a real QEMU (requires scripts/fetch-qemu.ps1).
/// Boots a machine-less QEMU with QMP on loopback and drives it via ZuTM's
/// own client — the same path the WinUI shell uses at runtime.
/// </summary>
public class RealQemuLifecycleTests
{
    private static QemuRuntime? FindQemu() => QemuRuntime.Discover();

    [E2EFact]
    public async Task QemuBoots_AndQmpControlsLifecycle()
    {
        var runtime = FindQemu();
        if (runtime is null || !File.Exists(runtime.SystemExecutable("x86_64")))
        {
            // Graceful no-op when QEMU is not fetched locally; CI always fetches it.
            return;
        }

        var qmpPort = PortAllocator.AllocateFreePort();
        using var qemu = Process.Start(new ProcessStartInfo(runtime.SystemExecutable("x86_64"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        qemu.StartInfo.ArgumentList.Add("-machine");
        qemu.StartInfo.ArgumentList.Add("none");
        qemu.StartInfo.ArgumentList.Add("-display");
        qemu.StartInfo.ArgumentList.Add("none");
        qemu.StartInfo.ArgumentList.Add("-qmp");
        qemu.StartInfo.ArgumentList.Add($"tcp:127.0.0.1:{qmpPort},server=on,wait=off");
        qemu.Start();
        Assert.NotNull(qemu);

        QmpClient? client = null;
        for (var attempt = 0; attempt < 40 && client is null; attempt++)
        {
            try
            {
                client = await QmpClient.ConnectAsync("127.0.0.1", qmpPort);
            }
            catch (SocketException)
            {
                await Task.Delay(250);
            }
        }

        Assert.NotNull(client);
        await using var connected = client!;

        var status = await connected.QueryStatusAsync();
        Assert.Equal("running", status);

        await connected.PauseAsync();
        Assert.Equal("paused", await connected.QueryStatusAsync());

        await connected.ResumeAsync();
        Assert.Equal("running", await connected.QueryStatusAsync());

        // Quit via QMP and observe an orderly exit.
        await connected.ExecuteAsync("quit");
        await WaitForExitAsync(qemu, expectedCode: 0);
    }

    [E2EFact]
    public async Task BundleRoundTripsThroughRealDisk_ThatUtmCanReopen()
    {
        // Create → save → reload → mutate → save → reload: the exact sequence
        // the app performs, against the real filesystem.
        var root = Directory.CreateTempSubdirectory("zutm-e2e-bundle-");
        var bundlePath = Path.Combine(root.FullName, "E2E VM.utm");

        var configuration = new UtmConfiguration
        {
            Information = new UtmInformation { Name = "E2E VM" },
            Drives =
            [
                new UtmDrive { ImageName = "data.qcow2", ImageType = UtmValues.DriveImageType.Disk, Interface = UtmValues.DriveInterface.Virtio },
            ],
        };

        var bundle = UtmBundle.CreateNew(bundlePath, configuration);
        File.WriteAllText(Path.Combine(bundle.DataDirectory.EnsureCreated(), "data.qcow2"), "stub-qcow2");
        bundle.Save();

        var reloaded = UtmBundle.Load(bundlePath);
        Assert.Equal("E2E VM", reloaded.Configuration.Information.Name);
        Assert.True(File.Exists(reloaded.ResolveDriveImagePath(reloaded.Configuration.Drives[0])));

        reloaded.Configuration = reloaded.Configuration with
        {
            System = reloaded.Configuration.System with { MemorySizeMib = 8192 },
        };
        reloaded.Save();

        var second = UtmBundle.Load(bundlePath);
        Assert.Equal(8192, second.Configuration.System.MemorySizeMib);
        Assert.Equal("stub-qcow2", await File.ReadAllTextAsync(second.ResolveDriveImagePath(second.Configuration.Drives[0])!));

        Directory.Delete(root.FullName, recursive: true);
    }

    private static async Task WaitForExitAsync(Process process, int expectedCode)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!process.HasExited)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(100);
        }

        Assert.Equal(expectedCode, process.ExitCode);
    }
}

internal static class DirectoryExtensions
{
    public static string EnsureCreated(this string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
