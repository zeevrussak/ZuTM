// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Core.Qemu;
using Xunit;

namespace ZuTM.Core.Tests.Qemu;

public class DiskImageFormatTests
{
    [Theory]
    [InlineData(DiskImageFormat.Qcow2, "qcow2")]
    [InlineData(DiskImageFormat.Raw, "raw")]
    [InlineData(DiskImageFormat.Vhd, "vpc")]
    [InlineData(DiskImageFormat.Vdi, "vdi")]
    [InlineData(DiskImageFormat.Vmdk, "vmdk")]
    public void QemuFormatName_MapsDriverNames(DiskImageFormat format, string expected) =>
        Assert.Equal(expected, format.QemuFormatName());

    [Theory]
    [InlineData(DiskImageFormat.Qcow2, ".qcow2")]
    [InlineData(DiskImageFormat.Raw, ".raw")]
    [InlineData(DiskImageFormat.Vhd, ".vhd")]
    [InlineData(DiskImageFormat.Vdi, ".vdi")]
    [InlineData(DiskImageFormat.Vmdk, ".vmdk")]
    public void FileExtension_MapsBundleExtensions(DiskImageFormat format, string expected) =>
        Assert.Equal(expected, format.FileExtension());

    [Theory]
    [InlineData(DiskImageFormat.Qcow2, false)]
    [InlineData(DiskImageFormat.Raw, false)]
    [InlineData(DiskImageFormat.Vhd, true)]
    [InlineData(DiskImageFormat.Vdi, true)]
    [InlineData(DiskImageFormat.Vmdk, false)]
    public void SupportsFixedAllocation_LimitedToVhdAndVdi(DiskImageFormat format, bool expected) =>
        Assert.Equal(expected, format.SupportsFixedAllocation());

    [Theory]
    [InlineData("disk-0.qcow2", "qcow2")]
    [InlineData("disk-0.QCOW2", "qcow2")]
    [InlineData("disk-0.qcow", "qcow2")]
    [InlineData("disk-0.vhd", "vpc")]
    [InlineData("disk-0.vdi", "vdi")]
    [InlineData("disk-0.vmdk", "vmdk")]
    [InlineData("disk-0.img", "raw")]
    [InlineData("disk-0.iso", "raw")]
    [InlineData("disk-0", "raw")]
    public void QemuFormatForFileName_InfersFromExtension(string fileName, string expected) =>
        Assert.Equal(expected, DiskImageExtensions.QemuFormatForFileName(fileName));
}

public class DiskImageCreatorArgumentTests
{
    private static DiskImageSpec Spec(DiskImageFormat format, DiskAllocationMode allocation) => new()
    {
        Path = Path.Combine(Path.GetTempPath(), "zutm-tests", "disk-0" + format.FileExtension()),
        SizeBytes = 20L * 1024 * 1024 * 1024,
        Format = format,
        Allocation = allocation,
    };

    private static string FullPath(DiskImageFormat format) =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "zutm-tests", "disk-0" + format.FileExtension()));

    [Fact]
    public void Qcow2Expanding_MinimalArguments()
    {
        var arguments = DiskImageCreator.BuildCreateArguments(Spec(DiskImageFormat.Qcow2, DiskAllocationMode.Expanding));

        Assert.Equal(
            ["create", "-f", "qcow2", FullPath(DiskImageFormat.Qcow2), "21474836480"],
            arguments);
    }

    [Fact]
    public void VdiFixed_UsesStaticPreallocation()
    {
        var arguments = DiskImageCreator.BuildCreateArguments(Spec(DiskImageFormat.Vdi, DiskAllocationMode.Fixed));

        Assert.Equal(
            ["create", "-f", "vdi", "-o", "static=on", FullPath(DiskImageFormat.Vdi), "21474836480"],
            arguments);
    }

    [Fact]
    public void VhdExpanding_PinsDynamicSubformat()
    {
        // Pinning the subformat keeps behavior independent of qemu-img defaults.
        var arguments = DiskImageCreator.BuildCreateArguments(Spec(DiskImageFormat.Vhd, DiskAllocationMode.Expanding));

        Assert.Equal(
            ["create", "-f", "vpc", "-o", "subformat=dynamic", FullPath(DiskImageFormat.Vhd), "21474836480"],
            arguments);
    }

    [Fact]
    public void VhdFixed_UsesFixedSubformat()
    {
        var arguments = DiskImageCreator.BuildCreateArguments(Spec(DiskImageFormat.Vhd, DiskAllocationMode.Fixed));

        Assert.Equal(
            ["create", "-f", "vpc", "-o", "subformat=fixed", FullPath(DiskImageFormat.Vhd), "21474836480"],
            arguments);
    }

    [Fact]
    public void VmdkFixed_IsRejected()
    {
        // qemu-img has no preallocation option for VMDK; the UI disables the
        // choice and the core refuses it so callers cannot pass a lie through.
        Assert.Throws<ArgumentException>(
            () => DiskImageCreator.BuildCreateArguments(Spec(DiskImageFormat.Vmdk, DiskAllocationMode.Fixed)));
    }

    [Theory]
    [InlineData(DiskImageFormat.Qcow2)]
    [InlineData(DiskImageFormat.Raw)]
    public void FixedAllocation_ForUnsupportedFormats_IsRejected(DiskImageFormat format)
    {
        // Windows qemu-img supports neither qcow2 preallocation=falloc/full nor
        // raw preallocation; the integration test below pins the real binary's
        // behavior for the formats that do preallocate (VHD, VDI).
        Assert.Throws<ArgumentException>(
            () => DiskImageCreator.BuildCreateArguments(Spec(format, DiskAllocationMode.Fixed)));
    }

    [Fact]
    public void VmdkExpanding_MinimalArguments()
    {
        var arguments = DiskImageCreator.BuildCreateArguments(Spec(DiskImageFormat.Vmdk, DiskAllocationMode.Expanding));

        Assert.Equal(
            ["create", "-f", "vmdk", FullPath(DiskImageFormat.Vmdk), "21474836480"],
            arguments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveSize_IsRejected(long sizeBytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskImageCreator.BuildCreateArguments(
            Spec(DiskImageFormat.Qcow2, DiskAllocationMode.Expanding) with { SizeBytes = sizeBytes }));

    [Fact]
    public void BlankPath_IsRejected() =>
        Assert.Throws<ArgumentException>(() => DiskImageCreator.BuildCreateArguments(
            Spec(DiskImageFormat.Qcow2, DiskAllocationMode.Expanding) with { Path = "  " }));
}

/// <summary>End-to-end qemu-img checks; silently skipped when no runtime is installed (CI machines without QEMU).</summary>
public class DiskImageCreatorQemuImgTests
{
    [Fact]
    public void Create_ExpandingStaysSparse_FixedAllocatesFully()
    {
        var runtime = QemuRuntime.Discover();
        if (runtime is null)
        {
            return; // argv contract is covered by DiskImageCreatorArgumentTests
        }

        var creator = new DiskImageCreator(runtime);
        var directory = Directory.CreateTempSubdirectory("zutm-diskimg-");
        try
        {
            var expanding = Path.Combine(directory.FullName, "expanding.qcow2");
            creator.Create(new DiskImageSpec
            {
                Path = expanding,
                SizeBytes = 64L * 1024 * 1024,
                Format = DiskImageFormat.Qcow2,
                Allocation = DiskAllocationMode.Expanding,
            });
            Assert.True(File.Exists(expanding));
            Assert.True(
                new FileInfo(expanding).Length < 4L * 1024 * 1024,
                $"expanding qcow2 should stay sparse but used {new FileInfo(expanding).Length} bytes");

            AssertFixedAllocatesFully(creator, directory.FullName, DiskImageFormat.Vdi);
            AssertFixedAllocatesFully(creator, directory.FullName, DiskImageFormat.Vhd);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void AssertFixedAllocatesFully(DiskImageCreator creator, string directory, DiskImageFormat format)
    {
        var path = Path.Combine(directory, "fixed" + format.FileExtension());
        creator.Create(new DiskImageSpec
        {
            Path = path,
            SizeBytes = 8L * 1024 * 1024,
            Format = format,
            Allocation = DiskAllocationMode.Fixed,
        });
        Assert.True(
            new FileInfo(path).Length >= 8L * 1024 * 1024,
            $"fixed {format} should allocate the full capacity but used {new FileInfo(path).Length} bytes");
    }
}
