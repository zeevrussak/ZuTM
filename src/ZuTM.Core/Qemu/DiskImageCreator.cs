// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Diagnostics;
using System.Globalization;

namespace ZuTM.Core.Qemu;

/// <summary>Disk container formats <c>qemu-img create</c> can produce and QEMU can attach.</summary>
public enum DiskImageFormat
{
    /// <summary>Copy-on-write, sparse, snapshot-capable — UTM's default.</summary>
    Qcow2,

    /// <summary>Plain flat image, no metadata (expanding only).</summary>
    Raw,

    /// <summary>Microsoft VHD (QEMU driver name: vpc).</summary>
    Vhd,

    /// <summary>VirtualBox disk image.</summary>
    Vdi,

    /// <summary>VMware disk image (expanding only).</summary>
    Vmdk,
}

/// <summary>How much host storage is committed when the image is created.</summary>
public enum DiskAllocationMode
{
    /// <summary>Sparse: the file grows as the guest writes (a.k.a. "dynamically allocated").</summary>
    Expanding,

    /// <summary>The full capacity is reserved on the host up front; no over-commit.</summary>
    Fixed,
}

/// <summary>Helpers mapping <see cref="DiskImageFormat"/> to qemu-img/QEMU names and file extensions.</summary>
public static class DiskImageExtensions
{
    /// <summary>Format name as used by qemu-img and <c>-drive format=</c>.</summary>
    public static string QemuFormatName(this DiskImageFormat format) => format switch
    {
        DiskImageFormat.Qcow2 => "qcow2",
        DiskImageFormat.Raw => "raw",
        DiskImageFormat.Vhd => "vpc",
        DiskImageFormat.Vdi => "vdi",
        DiskImageFormat.Vmdk => "vmdk",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>Bundle file extension ZuTM uses for newly created images.</summary>
    public static string FileExtension(this DiskImageFormat format) => format switch
    {
        DiskImageFormat.Qcow2 => ".qcow2",
        DiskImageFormat.Raw => ".raw",
        DiskImageFormat.Vhd => ".vhd",
        DiskImageFormat.Vdi => ".vdi",
        DiskImageFormat.Vmdk => ".vmdk",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>
    /// Whether the bundled Windows qemu-img can fully preallocate images of this
    /// format at creation time. Only formats that preallocate themselves accept it:
    /// VHD (<c>subformat=fixed</c>) and VDI (<c>static=on</c>). qcow2's
    /// <c>preallocation=falloc/full</c> and raw preallocation are unsupported in
    /// Windows QEMU builds, and VMDK has no preallocation option at all.
    /// </summary>
    public static bool SupportsFixedAllocation(this DiskImageFormat format) =>
        format is DiskImageFormat.Vhd or DiskImageFormat.Vdi;

    /// <summary>
    /// Infers the <c>-drive format=</c> value from an image file name. Unrecognized
    /// extensions fall back to raw, matching QEMU's own probe for flat images while
    /// never trusting content probing.
    /// </summary>
    public static string QemuFormatForFileName(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".qcow2" or ".qcow" => "qcow2",
            ".vdi" => "vdi",
            ".vhd" => "vpc",
            ".vmdk" => "vmdk",
            _ => "raw",
        };
}

/// <summary>Everything <c>qemu-img create</c> needs to produce a blank disk image.</summary>
public sealed record DiskImageSpec
{
    public required string Path { get; init; }

    /// <summary>Virtual size in bytes; the guest sees exactly this.</summary>
    public required long SizeBytes { get; init; }

    public DiskImageFormat Format { get; init; } = DiskImageFormat.Qcow2;

    public DiskAllocationMode Allocation { get; init; } = DiskAllocationMode.Expanding;
}

/// <summary>Creates blank disk images via the bundled <c>qemu-img.exe</c>.</summary>
public sealed class DiskImageCreator
{
    private readonly string _qemuImgPath;

    public DiskImageCreator(QemuRuntime runtime)
    {
        _qemuImgPath = Path.Combine(runtime.BinDirectory, "qemu-img.exe");
        if (!File.Exists(_qemuImgPath))
        {
            throw new FileNotFoundException("qemu-img.exe not found in the QEMU runtime.", _qemuImgPath);
        }
    }

    /// <summary>
    /// Pure argv builder for <c>qemu-img create</c>. Fixed allocation maps to the
    /// format's own preallocation switch — <c>subformat=fixed</c> (VHD) or
    /// <c>static=on</c> (VDI); VHD expanding pins <c>subformat=dynamic</c> so
    /// behavior never depends on qemu-img defaults.
    /// </summary>
    public static IReadOnlyList<string> BuildCreateArguments(DiskImageSpec spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spec.SizeBytes);
        if (spec.Allocation == DiskAllocationMode.Fixed && !spec.Format.SupportsFixedAllocation())
        {
            throw new ArgumentException(
                $"qemu-img cannot preallocate {spec.Format} images; use the expanding allocation.", nameof(spec));
        }

        List<string> arguments = ["create", "-f", spec.Format.QemuFormatName()];
        if (spec.Format == DiskImageFormat.Vhd)
        {
            arguments.AddRange(["-o", spec.Allocation == DiskAllocationMode.Fixed ? "subformat=fixed" : "subformat=dynamic"]);
        }
        else if (spec.Allocation == DiskAllocationMode.Fixed)
        {
            arguments.AddRange(["-o", "static=on"]);
        }

        arguments.Add(Path.GetFullPath(spec.Path));
        arguments.Add(spec.SizeBytes.ToString(CultureInfo.InvariantCulture));
        return arguments;
    }

    /// <summary>Runs qemu-img; throws with its stderr when creation fails.</summary>
    public void Create(DiskImageSpec spec)
    {
        // ArgumentList, never an interpolated command line: paths with quotes
        // or spaces cannot inject additional qemu-img arguments.
        var startInfo = new ProcessStartInfo(_qemuImgPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in BuildCreateArguments(spec))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        var stderr = process?.StandardError.ReadToEnd();
        process?.WaitForExit(30_000);
        if (process is null || process.ExitCode != 0)
        {
            throw new InvalidOperationException($"qemu-img failed: {stderr}");
        }
    }
}
