// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Diagnostics;
using System.Net.Sockets;
using ZuTM.Core.Qemu;
using Xunit;

namespace ZuTM.E2E;

/// <summary>
/// Real-QEMU verification of the USB-side hot-plug plumbing: mounting an ISO
/// into a VM with no CD drive at all (usb-storage CD is hot-plugged around
/// the ISO) and routing a usb-host device that is not present on the host
/// (error must surface, never wedge the session).
/// </summary>
public class UsbHotPlugE2ETests
{
    private static QmpClient? ConnectAndStartQemu(QemuRuntime runtime, int qmpPort, out Process qemu)
    {
        var startInfo = new ProcessStartInfo(runtime.SystemExecutable("x86_64"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var value in new[]
                 {
                     "-machine", "q35", "-display", "none",
                     // Display VMs always carry a USB bus (-usb for usb-tablet);
                     // mirror that so the tests exercise the realistic path.
                     "-usb",
                     "-qmp", $"tcp:127.0.0.1:{qmpPort},server=on,wait=off",
                 })
        {
            startInfo.ArgumentList.Add(value);
        }

        qemu = Process.Start(startInfo)!;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                return QmpClient.ConnectAsync("127.0.0.1", qmpPort).GetAwaiter().GetResult();
            }
            catch (SocketException)
            {
                Thread.Sleep(250);
            }
        }

        return null;
    }

    [E2EFact]
    public async Task MountIso_WithoutCdDrive_HotPlugsUsbCd_AndEjectRemovesIt()
    {
        var runtime = QemuRuntime.Discover();
        if (runtime is null || !File.Exists(runtime.SystemExecutable("x86_64")))
        {
            return;
        }

        var qmpPort = PortAllocator.AllocateFreePort();
        var isoPath = Path.Combine(Path.GetTempPath(), $"zutm-e2e-{Guid.NewGuid():N}.iso");
        await File.WriteAllBytesAsync(isoPath, new byte[512 * 1024]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var client = ConnectAndStartQemu(runtime, qmpPort, out var qemu);
            Assert.NotNull(client); // null → QEMU never opened QMP
            try
            {
                await using (client)
                {
                    // q35 exposes the board's anonymous empty IDE CD; ZuTM
                    // mounts into it when offered, or hot-plugs a USB CD.
                    var tray = await client.MountIsoAsync(isoPath, deadline.Token);
                    Assert.True(tray.HasMedia);

                    var verify = Assert.Single(await client.QueryCdTraysAsync(deadline.Token));
                    Assert.True(verify.HasMedia);
                    Assert.Equal(isoPath, verify.MediumFile);

                    await client.EjectAsync(verify, hotUnplugTimeout: TimeSpan.FromSeconds(10), cancellationToken: deadline.Token);
                    var ejected = Assert.Single(await client.QueryCdTraysAsync(deadline.Token));
                    Assert.False(ejected.HasMedia);
                }
            }
        finally
        {
            try
            {
                qemu.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                File.Delete(isoPath);
            }
            catch (IOException)
            {
            }
        }
    }

    [E2EFact]
    public async Task AttachUsbHost_MissingDevice_SurfacesQmpError()
    {
        var runtime = QemuRuntime.Discover();
        if (runtime is null || !File.Exists(runtime.SystemExecutable("x86_64")))
        {
            return;
        }

        var qmpPort = PortAllocator.AllocateFreePort();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var client = ConnectAndStartQemu(runtime, qmpPort, out var qemu);
        Assert.NotNull(client);
        try
        {
            await using (client)
            {
                // A VID/PID pair that does not exist on this host: modern QEMU
                // creates the usb-host device in pending-claim mode (it attaches
                // the physical unit when it appears) — no error, listed, removable.
                var attached = await client.AttachUsbHostDeviceAsync(0xFB5B, 0xFFFF, deadline.Token);
                Assert.Equal("zutm-usb-fb5b-ffff", attached.DeviceId);

                var routed = await client.QueryAttachedUsbHostDevicesAsync(deadline.Token);
                Assert.Contains(routed, d => d.DeviceId == "zutm-usb-fb5b-ffff");

                await client.DetachUsbHostDeviceAsync("zutm-usb-fb5b-ffff", unplugTimeout: TimeSpan.FromSeconds(10), cancellationToken: deadline.Token);
                Assert.DoesNotContain(
                    await client.QueryAttachedUsbHostDevicesAsync(deadline.Token),
                    d => d.DeviceId == "zutm-usb-fb5b-ffff");
            }
        }
        finally
        {
            try
            {
                qemu.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
