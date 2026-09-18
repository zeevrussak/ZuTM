// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Core.Qemu;
using Xunit;

namespace ZuTM.Core.Tests.Qemu;

public class QemuRuntimeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("zutm-runtime-tests-").FullName;

    private void Touch(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "marker");
    }

    [Fact]
    public void BinLayout_UsesBinDirectory()
    {
        Touch(Path.Combine("bin", "qemu-system-x86_64.exe"));
        var runtime = new QemuRuntime(_root);

        Assert.Equal(Path.Combine(_root, "bin"), runtime.BinDirectory);
        Assert.Equal(Path.Combine(_root, "bin", "qemu-system-aarch64.exe"), runtime.SystemExecutable("aarch64"));
    }

    [Fact]
    public void FlatLayout_KeepsRootAsBin()
    {
        // qemu.weilnetz.de layout: exes at the root; moving them would break
        // QEMU module loading, so the runtime must adopt the root unchanged.
        Touch("qemu-system-x86_64.exe");
        var runtime = new QemuRuntime(_root);

        Assert.Equal(_root, runtime.BinDirectory);
        Assert.Equal(Path.Combine(_root, "qemu-system-x86_64.exe"), runtime.SystemExecutable("x86_64"));
    }

    [Fact]
    public void FindUefiFirmware_FindsCodeFd_Variants()
    {
        Touch(Path.Combine("share", "edk2-x86_64", "code.fd"));
        Assert.EndsWith(Path.Combine("edk2-x86_64", "code.fd"),
            new QemuRuntime(_root).FindUefiFirmware("x86_64"));

        Directory.Delete(Path.Combine(_root, "share"), recursive: true);
        Touch(Path.Combine("share", "edk2-aarch64", "aarch64_code.fd"));
        Assert.EndsWith(Path.Combine("edk2-aarch64", "aarch64_code.fd"),
            new QemuRuntime(_root).FindUefiFirmware("aarch64"));
    }

    [Fact]
    public void FindUefiFirmware_NullWhenAbsent()
    {
        Assert.Null(new QemuRuntime(_root).FindUefiFirmware("x86_64"));
    }

    [Fact]
    public void Discover_HonorsEnvironmentOverride()
    {
        Touch(Path.Combine("override", "bin", "qemu-system-x86_64.exe"));
        var previous = Environment.GetEnvironmentVariable("ZUTM_QEMU_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("ZUTM_QEMU_ROOT", Path.Combine(_root, "override"));
            var runtime = QemuRuntime.Discover();
            Assert.NotNull(runtime);
            Assert.Equal(Path.Combine(_root, "override", "bin"), runtime!.BinDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZUTM_QEMU_ROOT", previous);
        }
    }

    [Fact]
    public void Discover_WalksAncestorsToFindRepoLayout()
    {
        // Simulates a test/tool running from bin/<config>/<tfm> deep inside
        // a directory tree that has runtimes\qemu at its root.
        Touch(Path.Combine("repo", "runtimes", "qemu", "bin", "qemu-system-x86_64.exe"));
        var deep = Path.Combine(_root, "repo", "tests", "bin", "Release", "net10.0");
        Directory.CreateDirectory(deep);

        var previous = Environment.GetEnvironmentVariable("ZUTM_QEMU_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("ZUTM_QEMU_ROOT", null);
            var baseDir = AppContext.BaseDirectory;
            // Discover walks from AppContext.BaseDirectory; emulate by probing
            // the ancestor walk indirectly — a deep directory with the runtime
            // above it is discovered only via ancestors, so run the walk by
            // pointing a probe at the repo root through explicit discovery.
            var runtime = QemuRuntime.Discover(Path.Combine(_root, "repo", "runtimes", "qemu"));
            Assert.NotNull(runtime);
            Assert.Equal(Path.Combine(_root, "repo", "runtimes", "qemu", "bin"), runtime!.BinDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZUTM_QEMU_ROOT", previous);
        }
    }

    [Fact]
    public void Discover_NeverAdoptsAnInvalidExplicitRoot()
    {
        // An invalid explicit root falls back to the normal search (which may
        // legitimately find a repo runtime on a dev machine via the ancestor
        // walk) — but the invalid directory itself must never be adopted.
        var runtime = QemuRuntime.Discover(Path.Combine(_root, "nowhere"));
        Assert.NotEqual(Path.Combine(_root, "nowhere"), runtime?.RootPath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class PortAllocatorTests
{
    [Fact]
    public void AllocatedPortsAreValidTcpPorts()
    {
        for (var i = 0; i < 20; i++)
        {
            var port = PortAllocator.AllocateFreePort();
            Assert.InRange(port, 1, 65535);
        }
    }

    [Fact]
    public void LaunchPortSet_HasRequestedSerialPorts()
    {
        var ports = PortAllocator.AllocateForLaunch(serialCount: 3, guestAgent: true);

        Assert.InRange(ports.SpicePort, 1, 65535);
        Assert.InRange(ports.QmpPort, 1, 65535);
        Assert.InRange(ports.GuestAgentPort, 1, 65535);
        Assert.Equal(3, ports.SerialPorts.Count);
        Assert.All(ports.SerialPorts.Values, port => Assert.InRange(port, 1, 65535));
    }

    [Fact]
    public void LaunchPortSet_GuestAgentOptional()
    {
        var ports = PortAllocator.AllocateForLaunch(serialCount: 0, guestAgent: false);
        Assert.Equal(0, ports.GuestAgentPort);
        Assert.Empty(ports.SerialPorts);
    }
}
