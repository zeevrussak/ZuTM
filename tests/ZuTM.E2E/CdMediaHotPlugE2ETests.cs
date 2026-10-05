// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Diagnostics;
using System.Net.Sockets;
using ZuTM.Core.Qemu;
using Xunit;

namespace ZuTM.E2E;

/// <summary>
/// Real-QEMU verification of the viewer's runtime ISO mount/eject: the exact
/// launch arguments the builder now emits (stable zutm-cd-dev-N frontend id,
/// empty tray) followed by the QMP blockdev medium sequence.
/// </summary>
public class CdMediaHotPlugE2ETests
{
    private static QemuRuntime? FindQemu() => QemuRuntime.Discover();

    [E2EFact]
    public async Task EmptyTrayLaunch_MountEjectIso_LiveOverQmp()
    {
        var runtime = FindQemu();
        if (runtime is null || !File.Exists(runtime.SystemExecutable("x86_64")))
        {
            return; // graceful no-op when QEMU is not fetched locally; CI always fetches it
        }

        var qmpPort = PortAllocator.AllocateFreePort();
        var isoPath = Path.Combine(Path.GetTempPath(), $"zutm-e2e-{Guid.NewGuid():N}.iso");
        await File.WriteAllBytesAsync(isoPath, new byte[1024 * 1024]); // raw "ISO" — block-level only

        // Hard ceiling so a wedged QEMU or QMP reply fails instead of hanging CI.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        // Mirror QemuCommandLineBuilder.AddCdDrive output for an empty IDE CD —
        // arguments must be on the ProcessStartInfo BEFORE the process starts.
        var startInfo = new ProcessStartInfo(runtime.SystemExecutable("x86_64"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var value in new[]
                 {
                     "-machine", "q35", "-display", "none",
                     "-drive", "id=zutm-cd-0,if=none,media=cdrom",
                     "-device", "ide-cd,drive=zutm-cd-0,id=zutm-cd-dev-0",
                     "-qmp", $"tcp:127.0.0.1:{qmpPort},server=on,wait=off",
                 })
        {
            startInfo.ArgumentList.Add(value);
        }

        using var qemu = Process.Start(startInfo)!;

        try
        {
            QmpClient? client = null;
            for (var attempt = 0; attempt < 40 && client is null; attempt++)
            {
                try
                {
                    client = await QmpClient.ConnectAsync("127.0.0.1", qmpPort, deadline.Token);
                }
                catch (SocketException)
                {
                    await Task.Delay(250, deadline.Token);
                }
                catch (Exception ex) when (ex is EndOfStreamException or QmpException)
                {
                    Assert.Fail($"QMP handshake failed — QEMU rejected the launch args: {ex.Message}");
                }
            }

            Assert.NotNull(client); // QEMU came up with the CD tray arguments
            await using (client)
            {
                var trays = await client.QueryCdTraysAsync(deadline.Token);
                var tray = Assert.Single(trays);
                Assert.Equal("zutm-cd-dev-0", tray.DeviceId);
                Assert.False(tray.HasMedia);

                var mounted = await client.MountIsoAsync(tray, isoPath, deadline.Token);
                Assert.True(mounted.HasMedia);
                Assert.Equal(isoPath, mounted.MediumFile);

                var verify = Assert.Single(await client.QueryCdTraysAsync(deadline.Token));
                Assert.True(verify.HasMedia);
                Assert.Equal(isoPath, verify.MediumFile);

                await client.EjectAsync(verify, cancellationToken: deadline.Token);
                var ejected = Assert.Single(await client.QueryCdTraysAsync(deadline.Token));
                Assert.False(ejected.HasMedia);

                // Remount to prove the tray is reusable after an eject.
                var remounted = await client.MountIsoAsync(ejected, isoPath, deadline.Token);
                Assert.True(remounted.HasMedia);
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
}
