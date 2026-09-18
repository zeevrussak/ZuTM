// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

namespace ZuTM.Core.Qemu;

/// <summary>Where the QEMU installation that ZuTM drives lives.</summary>
public sealed class QemuRuntime
{
    /// <summary>Root of the QEMU installation.</summary>
    public string RootPath { get; }

    private readonly string _binDirectory;

    public QemuRuntime(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);

        // Two layouts exist: installers that keep executables at the root
        // (qemu.weilnetz.de — QEMU resolves its modules relative to the exe,
        // so this layout must be preserved) and distributions that use bin\.
        _binDirectory = File.Exists(Path.Combine(RootPath, "qemu-system-x86_64.exe"))
            ? RootPath
            : Path.Combine(RootPath, "bin");
    }

    public string BinDirectory => _binDirectory;

    public string ShareDirectory => Path.Combine(RootPath, "share");

    /// <summary>Path of the qemu-system executable for a guest architecture (qemu-system-x86_64.exe, qemu-system-aarch64.exe, …).</summary>
    public string SystemExecutable(string architecture) =>
        Path.Combine(BinDirectory, $"qemu-system-{architecture}.exe");

    /// <summary>UEFI firmware for a guest architecture, or null when not installed.</summary>
    public string? FindUefiFirmware(string architecture)
    {
        // Layout used by ZuTM's bundled QEMU: share/edk2-<arch>/{code,vars}.fd
        var edk2 = Path.Combine(ShareDirectory, $"edk2-{architecture}");
        var candidates = new[]
        {
            Path.Combine(edk2, "code.fd"),
            Path.Combine(edk2, $"{architecture}_code.fd"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>True when a directory plausibly holds a QEMU runtime (bin\ layout or flat installer layout).</summary>
    private static bool LooksLikeRuntime(string directory) =>
        Directory.Exists(Path.Combine(directory, "bin"))
        || File.Exists(Path.Combine(directory, "qemu-system-x86_64.exe"))
        || File.Exists(Path.Combine(directory, "bin", "qemu-system-x86_64.exe"));

    /// <summary>Default search order for a QEMU runtime on this machine.</summary>
    public static QemuRuntime? Discover(string? explicitRoot = null)
    {
        IEnumerable<string> roots =
        [
            .. (explicitRoot is null ? [] : new[] { explicitRoot }),
            // Environment override first — power users and CI pin it here.
            Environment.GetEnvironmentVariable("ZUTM_QEMU_ROOT") ?? "",
        ];

        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            if (LooksLikeRuntime(root))
            {
                return new QemuRuntime(root);
            }
        }

        // Bundled app layout: <appdir>\runtimes\qemu
        var bundled = Path.Combine(AppContext.BaseDirectory, "runtimes", "qemu");
        if (LooksLikeRuntime(bundled))
        {
            return new QemuRuntime(bundled);
        }

        // Repo layout during development: tests/tools run from bin/<config>/<tfm>
        // several levels deep — walk ancestors until a runtimes\qemu shows up.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "runtimes", "qemu");
            if (LooksLikeRuntime(candidate))
            {
                return new QemuRuntime(candidate);
            }
        }

        return null;
    }
}
