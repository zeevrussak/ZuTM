// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
//
// E2E for the `zutm` CLI: the same launch path (VmLauncher + RuntimeRegistry)
// the GUI uses, driven from a second process — create/list/show/clone/delete,
// plus a real headless-VM start/stop round-trip through QMP.

using System.Diagnostics;
using ZuTM.Core.Qemu;
using ZuTM.Core.Utm;
using Xunit;

namespace ZuTM.E2E;

public class CliTests : AlpineVmTestBase
{
    private static string CliExe => Path.GetFullPath(Path.Combine(
        TestImagesDir, "..", "src", "ZuTM.Cli", "bin", "Release", "net10.0", "zutm.exe"));

    private static string Quote(string value) => $"\"{value}\"";

    private static (string Stdout, string Stderr, int ExitCode) RunCli(params string[] arguments)
    {
        // Route through cmd.exe with file redirection: QEMU (spawned by the
        // CLI) inherits the CLI's stdio handles, so pipe-based reads never
        // see EOF while the VM runs. Files sidestep handle inheritance.
        if (!File.Exists(CliExe))
        {
            Assert.Fail($"CLI not built at {CliExe}; build the solution first.");
        }

        var stdoutPath = Path.Combine(Path.GetTempPath(), $"zutm-cli-out-{Guid.NewGuid():N}.txt");
        var stderrPath = Path.Combine(Path.GetTempPath(), $"zutm-cli-err-{Guid.NewGuid():N}.txt");
        var startInfo = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            // Outer quotes: cmd /c with a leading-quoted command needs the
            // whole string re-quoted or the first quote is mis-paired.
            $"/c \"\"{CliExe}\" {string.Join(' ', arguments.Select(a => a.Contains(' ') ? Quote(a) : a))} > {Quote(stdoutPath)} 2> {Quote(stderrPath)}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)!;
        if (!process.WaitForExit(240_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        // QEMU (grandchild) inherits the redirection file handles; read with
        // permissive sharing instead of exclusive open.
        var stdout = ReadShared(stdoutPath);
        var stderr = ReadShared(stderrPath);
        try
        {
            File.Delete(stdoutPath);
            File.Delete(stderrPath);
        }
        catch (IOException)
        {
        }

        return (stdout, stderr, process.HasExited ? process.ExitCode : -1);
    }

    private static string ReadShared(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return string.Empty;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
        }

        return string.Empty;
    }

    private string TempVmFolder => Path.Combine(TempDir, "vms");

    private string CreateBundle(string name)
    {
        Directory.CreateDirectory(TempVmFolder);
        var bundle = UtmBundle.CreateNew(Path.Combine(TempVmFolder, name + ".utm"),
            new UtmConfiguration { Information = new UtmInformation { Name = name } });
        bundle.Save();
        return bundle.Id.ToString("D");
    }

    [E2EFact]
    public void ListShowCloneDelete_WorkEndToEnd()
    {
        var uuid = CreateBundle("cli-alpha");

        var list = RunCli("list", "--folder", TempVmFolder);
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("cli-alpha", list.Stdout);
        Assert.Contains("stopped", list.Stdout);

        var show = RunCli("show", "cli-alpha", "--folder", TempVmFolder);
        Assert.Equal(0, show.ExitCode);
        Assert.Contains(uuid, show.Stdout);

        var clone = RunCli("clone", "cli-alpha", "--folder", TempVmFolder);
        Assert.Equal(0, clone.ExitCode);
        Assert.Contains("cloned to", clone.Stdout);
        Assert.True(Directory.Exists(Path.Combine(TempVmFolder, "cli-alpha copy.utm")));

        var delete = RunCli("delete", "cli-alpha copy", "--folder", TempVmFolder);
        Assert.Equal(0, delete.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(TempVmFolder, "cli-alpha copy.utm")));

        var missing = RunCli("show", "no-such-vm", "--folder", TempVmFolder);
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("no VM named", missing.Stderr);
    }

    [E2EFact]
    public async Task StartStop_RealHeadlessVm_ThroughCliAndRegistry()
    {
        if (FindQemu() is null || !ImageReady("alpine-headless.qcow2"))
        {
            return;
        }

        // Build a VM around the golden headless disk image.
        Directory.CreateDirectory(TempVmFolder);
        var vm = UtmBundle.CreateNew(Path.Combine(TempVmFolder, "cli-vm.utm"), new UtmConfiguration
        {
            Information = new UtmInformation { Name = "cli-vm" },
            System = new UtmSystem { Architecture = "x86_64", Target = "q35", MemorySizeMib = 512, CpuCount = 2 },
            Serials = [new UtmSerial { Mode = UtmValues.SerialMode.Terminal, Target = UtmValues.SerialTarget.Auto }],
            Drives =
            [
                new UtmDrive
                {
                    ImageName = "disk.qcow2",
                    ImageType = UtmValues.DriveImageType.Disk,
                    Interface = UtmValues.DriveInterface.Virtio,
                },
            ],
        });
        Directory.CreateDirectory(vm.DataDirectory);
        File.Copy(Path.Combine(TestImagesDir, "alpine-headless.qcow2"), Path.Combine(vm.DataDirectory, "disk.qcow2"));
        vm.Save();

        var start = RunCli("start", "cli-vm", "--folder", TempVmFolder);
        Assert.True(start.ExitCode == 0 && start.Stdout.Contains("started cli-vm"),
            $"start failed rc={start.ExitCode} out=[{start.Stdout}] err=[{start.Stderr}]");

        try
        {
            // The registry (same file the CLI wrote) lets this process talk QMP.
            var entry = new RuntimeRegistry().Find("cli-vm");
            Assert.NotNull(entry);
            await using var qmp = await QmpClient.ConnectAsync("127.0.0.1", entry!.QmpPort);
            Assert.Equal("running", await qmp.QueryStatusAsync());

            var list = RunCli("list", "--folder", TempVmFolder);
            Assert.Contains("running", list.Stdout);
            Assert.Contains("cli-vm", list.Stdout);
        }
        finally
        {
            var stop = RunCli("stop", "cli-vm", "--folder", TempVmFolder);
            Assert.True(stop.ExitCode == 0,
                $"stop failed rc={stop.ExitCode} out=[{stop.Stdout}] err=[{stop.Stderr}]");

            // Registry entry must clear once QEMU exits (kill fallback inside
            // the CLI covers ACPI-less guests; stale-pid pruning clears the file).
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            while (true)
            {
                var stillRunning = new RuntimeRegistry().Find("cli-vm");
                if (stillRunning is null)
                {
                    break;
                }

                // Belt and braces: if the CLI's kill somehow missed, finish it.
                try
                {
                    System.Diagnostics.Process.GetProcessById(stillRunning.Pid).Kill(entireProcessTree: true);
                }
                catch (ArgumentException)
                {
                    // already gone — pruning will clear it on the next load
                }

                cts.Token.ThrowIfCancellationRequested();
                await Task.Delay(1000, cts.Token);
            }
        }
    }
}

/// <summary>
/// UI smoke automation: launches the built app and verifies its main window
/// appears, then closes it. Opt-in via ZUTM_UI_E2E=1 and ZUTM_APP_EXE pointing
/// at a built ZuTM.exe (publish output or msix-unpacked bin).
/// </summary>
public sealed class UiAppSmokeTests
{
    [Fact]
    public void AppLaunchesMainWindow()
    {
        if (Environment.GetEnvironmentVariable("ZUTM_UI_E2E") != "1"
            || Environment.GetEnvironmentVariable("ZUTM_APP_EXE") is not { Length: > 0 } appExe)
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo(appExe) { UseShellExecute = true });
        Assert.NotNull(process);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                process.Refresh();
                if (process.MainWindowTitle.Contains("ZuTM", StringComparison.Ordinal))
                {
                    return; // window up — smoke passed
                }

                Assert.False(process.HasExited, "app exited before showing a window");
                Thread.Sleep(500);
            }

            Assert.Fail("main window did not appear within 30 s");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
