// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

namespace ZuTM.Core.Qemu;

/// <summary>Where the QEMU installation that ZuTM drives lives.</summary>
public sealed class QemuRuntime
{
    /// <summary>Root of the QEMU installation (contains bin\, share\, …).</summary>
    public string RootPath { get; }

    public QemuRuntime(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
    }

    public string BinDirectory => Path.Combine(RootPath, "bin");

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

    /// <summary>Default search order for a QEMU runtime on this machine.</summary>
    public static QemuRuntime? Discover(string? explicitRoot = null)
    {
        IEnumerable<string> roots =
        [
            .. (explicitRoot is null ? [] : new[] { explicitRoot }),
            // Bundled with the installed app: <appdir>\runtimes\qemu
            Path.Combine(AppContext.BaseDirectory, "runtimes", "qemu"),
            // Repo layout during development
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "runtimes", "qemu"),
            // Environment override for power users
            Environment.GetEnvironmentVariable("ZUTM_QEMU_ROOT") ?? "",
        ];

        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            if (Directory.Exists(Path.Combine(root, "bin")))
            {
                return new QemuRuntime(root);
            }
        }

        return null;
    }
}
