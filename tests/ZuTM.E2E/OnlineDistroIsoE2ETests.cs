// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
//
// End-to-end for the creation flow the New-VM dialog drives when the user
// picks a distribution from the online catalog: resolve the official ISO URL
// from the distributor's own index, download it into a real .utm bundle with
// progress reporting and .part staging, then boot the downloaded image
// through ZuTM's shared launcher and prove the guest reaches a login prompt
// over serial. Alpine "virt" is the catalog's small entry (~60 MiB) so the
// download stays quick.
//
// The stock ISO's bootloader has no serial console, so the boot half
// direct-kernel-boots the kernel/initrd pulled out of the very ISO that was
// downloaded (minimal ISO 9660 + Rock Ridge NM reader below) — always
// version-exact, no external unpacker tool.

using System.Text.RegularExpressions;
using ZuTM.Core.Net;
using ZuTM.Core.Qemu;
using ZuTM.Core.Utm;
using Xunit;

namespace ZuTM.E2E;

public class OnlineDistroIsoE2ETests : AlpineVmTestBase
{
    private const string OfferingId = "alpine-virt-x86_64";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Resolve → download into a bundle → boot the downloaded ISO → login →
    /// poweroff. Download and boot are one test because the point is the
    /// chain: an online-sourced image must produce a bootable VM.
    /// </summary>
    [E2EFact]
    public async Task ResolvesOfficialIso_DownloadsIntoBundle_AndBootsToLogin()
    {
        var runtime = FindQemu();
        if (runtime is null)
        {
            return; // graceful no-op without QEMU; CI always fetches it
        }

        var offering = Assert.Single(DistroIsoCatalog.All, o => o.Id == OfferingId);

        // 1. Resolve the current release from the distributor's index.
        var url = await new DistroIsoResolver().ResolveUrlAsync(offering);
        Assert.StartsWith("https://dl-cdn.alpinelinux.org/", url);
        Assert.Null(RemoteImage.GetValidationError(url));
        var version = Regex.Match(url, @"alpine-virt-(?<ver>\d+\.\d+\.\d+)-x86_64\.iso").Groups["ver"].Value;
        Assert.NotEqual(string.Empty, version);

        // 2. Create a real bundle and download exactly the way NewVmDialog
        //    does: ImportRemoteIsoAsync with progress and .part staging.
        var configuration = new UtmConfiguration
        {
            Information = new UtmInformation { Name = "online-alpine" },
            System = new UtmSystem { Architecture = "x86_64", Target = "q35", MemorySizeMib = 512, CpuCount = 2 },
            Serials = [new UtmSerial { Mode = UtmValues.SerialMode.Terminal, Target = UtmValues.SerialTarget.Auto }],
            Displays = [new UtmDisplay { Hardware = "virtio-gpu-pci" }],
            Networks = [new UtmNetwork { Mode = UtmValues.NetworkMode.Shared, Hardware = "virtio-net-pci" }],
        };
        var bundle = UtmBundle.CreateNew(Path.Combine(TempDir, "online-alpine.utm"), configuration);
        Directory.CreateDirectory(bundle.DataDirectory);
        bundle.Save();

        var progressReports = new List<RemoteIsoDownloadProgress>();
        var progress = new Progress<RemoteIsoDownloadProgress>(progressReports.Add);
        using var downloadCts = new CancellationTokenSource(DownloadTimeout);
        var cd = await bundle.ImportRemoteIsoAsync(url, UtmValues.DriveInterface.Ide, progress, cancellationToken: downloadCts.Token);

        // The downloaded image must be the CD, read-only, complete (no .part
        // leftover), and the progress stream must agree with the file on disk.
        Assert.Equal(UtmValues.DriveImageType.Cd, cd.ImageType);
        Assert.True(cd.IsReadOnly);
        var isoPath = Path.Combine(bundle.DataDirectory, cd.ImageName!);
        Assert.True(File.Exists(isoPath));
        Assert.False(File.Exists(isoPath + ".part"));
        var isoLength = new FileInfo(isoPath).Length;
        Assert.True(isoLength > 32 * 1024 * 1024, $"ISO suspiciously small ({isoLength} B) — download truncated");
        Assert.Equal(version, ReadIsoVolumeIdVersion(isoPath)); // really the resolved release
        await WaitForProgressCompletionAsync(progressReports, isoLength);

        // 3. Blank data disk, like the dialog's disk-size step.
        var diskPath = Path.Combine(bundle.DataDirectory, "online-alpine-0.qcow2");
        new DiskImageCreator(runtime).Create(new DiskImageSpec
        {
            Path = diskPath,
            SizeBytes = 64 * 1024 * 1024,
            Format = DiskImageFormat.Qcow2,
            Allocation = DiskAllocationMode.Expanding,
        });
        Assert.True(File.Exists(diskPath));

        // 4. Wire the bundle the way the dialog would have saved it: CD first
        //    (boot order), then the blank disk, plus the direct-kernel-boot
        //    files from the downloaded ISO and the serial-console cmdline.
        var kernelPath = Path.Combine(TempDir, "vmlinuz-virt");
        var initrdPath = Path.Combine(TempDir, "initramfs-virt");
        ExtractIsoFile(isoPath, "boot/vmlinuz-virt", kernelPath);
        ExtractIsoFile(isoPath, "boot/initramfs-virt", initrdPath);

        var kernelDriveId = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var initrdDriveId = Guid.NewGuid().ToString("D").ToUpperInvariant();
        bundle.State = bundle.State with
        {
            ExternalDrivePaths = new Dictionary<string, string>
            {
                [kernelDriveId] = kernelPath,
                [initrdDriveId] = initrdPath,
            },
        };
        bundle.Configuration = bundle.Configuration with
        {
            Qemu = bundle.Configuration.Qemu with
            {
                AdditionalArguments =
                [
                    new UtmQemuArgument(["-append", "console=ttyS0,115200 modules=loop,squashfs,sd-mod,usb-storage quiet"]),
                ],
            },
            Drives =
            [
                cd,
                new UtmDrive
                {
                    ImageName = Path.GetFileName(diskPath),
                    ImageType = UtmValues.DriveImageType.Disk,
                    Interface = UtmValues.DriveInterface.Virtio,
                },
                new UtmDrive { ImageType = UtmValues.DriveImageType.LinuxKernel, Identifier = kernelDriveId },
                new UtmDrive { ImageType = UtmValues.DriveImageType.LinuxInitrd, Identifier = initrdDriveId },
            ],
        };
        bundle.Save();

        // 5. Boot through the shared launcher (the GUI/CLI start path) and
        //    prove the downloaded image is a working live system over serial.
        var launcher = new VmLauncher(runtime, new RuntimeRegistry());
        var result = await launcher.StartAsync(bundle);
        await using (result.Process.Lease())
        {
            Assert.NotEqual(VmRunState.Stopped, result.Process.State); // QEMU alive during boot

            await using var console = await ConnectSerialAsync(result.Ports.SerialPorts[0]);
            await console.LoginAsync("root", password: null, TimeSpan.FromMinutes(5));

            var release = await console.RunAsync("cat /etc/alpine-release", "E2E-REL", TimeSpan.FromSeconds(30));
            Assert.Equal(version, release.Trim());

            Assert.Equal("running", await result.Process.Qmp!.QueryStatusAsync());

            await console.SendLineAsync("poweroff");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            while (result.Process.State != VmRunState.Stopped)
            {
                await Task.Delay(250, cts.Token);
            }
        }
    }

    /// <summary>
    /// Version in the ISO 9660 primary volume descriptor (PVD at byte 32768,
    /// volume identifier 32 bytes at +40), e.g. "alpine-virt 3.24.2 x86_64".
    /// </summary>
    private static string ReadIsoVolumeIdVersion(string isoPath)
    {
        using var stream = File.OpenRead(isoPath);
        stream.Position = 32768 + 40;
        Span<byte> buffer = stackalloc byte[32];
        var read = stream.Read(buffer);
        var identifier = System.Text.Encoding.ASCII.GetString(buffer[..read]).TrimEnd(' ');
        return Regex.Match(identifier, @"\d+\.\d+\.\d+").Value;
    }

    // -- Minimal ISO 9660 reader (with Rock Ridge NM names) -----------------------

    private const int SectorSize = 2048;

    private readonly record struct IsoExtent(uint StartLba, uint Length)
    {
        public long ByteOffset => StartLba * SectorSize;
    }

    /// <summary>Extracts one file (e.g. "boot/vmlinuz-virt") from an ISO 9660 image.</summary>
    private static void ExtractIsoFile(string isoPath, string innerPath, string destinationPath)
    {
        using var iso = File.OpenRead(isoPath);
        var root = ReadRootExtent(iso);
        var segments = innerPath.Split('/');
        var extent = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var last = i == segments.Length - 1;
            var entry = FindDirectoryEntry(iso, extent, segments[i], wantDirectory: !last)
                ?? throw new InvalidOperationException($"'{innerPath}' not found in the downloaded ISO");
            extent = entry;
        }

        using (var destination = File.Create(destinationPath))
        {
            iso.Position = extent.ByteOffset;
            var buffer = new byte[1024 * 1024];
            for (long remaining = extent.Length; remaining > 0;)
            {
                var chunk = (int)Math.Min(remaining, buffer.Length);
                Assert.Equal(chunk, ReadExactly(iso, buffer.AsSpan(0, chunk)));
                destination.Write(buffer, 0, chunk);
                remaining -= chunk;
            }
        }
    }

    private static IsoExtent ReadRootExtent(FileStream iso)
    {
        Span<byte> pvd = stackalloc byte[SectorSize];
        iso.Position = 32768;
        Assert.Equal(SectorSize, ReadExactly(iso, pvd));
        Assert.Equal(1, pvd[0]); // primary volume descriptor
        Assert.Equal("CD001"u8.ToArray(), pvd[1..6].ToArray());
        return DecodeRecord(pvd[156..]);
    }

    private static IsoExtent? FindDirectoryEntry(FileStream iso, IsoExtent directory, string name, bool wantDirectory)
    {
        var buffer = new byte[directory.Length];
        iso.Position = directory.ByteOffset;
        Assert.Equal(buffer.Length, ReadExactly(iso, buffer));

        var offset = 0;
        while (offset < buffer.Length)
        {
            var recordLength = buffer[offset];
            if (recordLength == 0)
            {
                // Records do not cross sector boundaries; skip the padding.
                offset = (offset / SectorSize + 1) * SectorSize;
                continue;
            }

            var record = buffer.AsSpan(offset, recordLength);
            offset += recordLength;

            var isDirectory = (record[25] & 0b1) != 0;
            if (isDirectory != wantDirectory)
            {
                continue;
            }

            var entryName = RecordName(record);
            if (entryName is null || entryName is "." or "..")
            {
                continue;
            }

            if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase)
                || Normalized(entryName) == Normalized(name))
            {
                return DecodeRecord(record);
            }
        }

        return null;
    }

    /// <summary>Rock Ridge NM name when present, else the plain ISO 9660 name ("FILE.;1" → "file").</summary>
    private static string? RecordName(ReadOnlySpan<byte> record)
    {
        var nameLength = record[32];
        var name = System.Text.Encoding.ASCII.GetString(record[33..(33 + nameLength)]);
        if (name is "\0" or "\1")
        {
            return name == "\0" ? "." : "..";
        }

        var systemUse = record[(33 + nameLength + ((nameLength % 2 == 0) ? 1 : 0))..];
        for (var offset = 0; offset + 4 <= systemUse.Length;)
        {
            var entryLength = systemUse[offset + 2];
            if (entryLength < 4 || offset + entryLength > systemUse.Length)
            {
                break;
            }

            if (systemUse[offset] == (byte)'N' && systemUse[offset + 1] == (byte)'M' && entryLength > 5)
            {
                // NM: flags at +4, POSIX name from +5.
                return System.Text.Encoding.ASCII.GetString(systemUse[(offset + 5)..(offset + entryLength)]);
            }

            offset += entryLength;
        }

        var semicolon = name.IndexOf(';');
        if (semicolon >= 0)
        {
            name = name[..semicolon];
        }

        return name.TrimEnd('.').ToLowerInvariant();
    }

    private static string Normalized(string name) => name.Replace('-', '_').ToLowerInvariant();

    private static IsoExtent DecodeRecord(ReadOnlySpan<byte> record)
    {
        var startLba = BitConverter.ToUInt32(record[2..6]);
        var length = BitConverter.ToUInt32(record[10..14]);
        return new IsoExtent(startLba, length);
    }

    private static int ReadExactly(FileStream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>Progress<T> marshals via the thread pool — poll until the final byte count shows up (or fail).</summary>
    private static async Task WaitForProgressCompletionAsync(List<RemoteIsoDownloadProgress> reports, long totalBytes)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (reports.All(p => p.BytesReceived != totalBytes))
        {
            await Task.Delay(200, cts.Token);
        }

        Assert.True(reports.Count >= 2, "no intermediate progress was reported");
        Assert.NotNull(reports[^1].Percent);
    }

    private static async Task<SerialConsoleSession> ConnectSerialAsync(int port)
    {
        SerialConsoleSession? session = null;
        for (var attempt = 0; attempt < 40 && session is null; attempt++)
        {
            try
            {
                session = await SerialConsoleSession.ConnectAsync("127.0.0.1", port);
            }
            catch (System.Net.Sockets.SocketException)
            {
                await Task.Delay(250);
            }
        }

        Assert.NotNull(session);
        return session!;
    }
}
