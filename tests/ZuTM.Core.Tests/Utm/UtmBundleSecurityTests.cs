// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
// Security regressions: config.plist comes from arbitrary (possibly hostile)
// bundles — its file names must never steer reads/deletes outside Data/.

using ZuTM.Core.Utm;
using Xunit;

namespace ZuTM.Core.Tests.Utm;

public class UtmBundleSecurityTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("zutm-sec-tests-").FullName;

    private string BundlePath => Path.Combine(_tempRoot, "Victim.utm");

    private UtmBundle CreateBundleWithImageName(string imageName, out string sentinelPath)
    {
        sentinelPath = Path.Combine(_tempRoot, "sentinel.txt");
        File.WriteAllText(sentinelPath, "do-not-touch");

        var configuration = new UtmConfiguration
        {
            Information = new UtmInformation { Name = "Victim" },
            Drives =
            [
                new UtmDrive { ImageName = imageName, ImageType = UtmValues.DriveImageType.Disk, Interface = UtmValues.DriveInterface.Virtio },
            ],
        };
        var bundle = UtmBundle.CreateNew(BundlePath, configuration);
        bundle.Save();
        return bundle;
    }

    [Theory]
    [InlineData("..\\..\\sentinel.txt")]
    [InlineData("../../sentinel.txt")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("sub\\dir\\disk.qcow2")]
    [InlineData("C:\\Windows\\evil")]
    [InlineData("disk:stream")]
    public void ResolveDriveImagePath_RefusesTraversalNames(string imageName)
    {
        var bundle = CreateBundleWithImageName(imageName, out var sentinel);

        Assert.Null(bundle.ResolveDriveImagePath(bundle.Configuration.Drives[0]));
        Assert.True(File.Exists(sentinel)); // nothing was touched
    }

    [Theory]
    [InlineData("..\\..\\sentinel.txt")]
    [InlineData("../sentinel.txt")]
    [InlineData("..")]
    [InlineData("sub/disk.qcow2")]
    public void DeleteDriveImage_RefusesTraversalNames(string imageName)
    {
        var bundle = CreateBundleWithImageName(imageName, out var sentinel);

        bundle.DeleteDriveImage(bundle.Configuration.Drives[0]);

        Assert.True(File.Exists(sentinel), "delete must not remove files outside the bundle");
    }

    [Fact]
    public void ResolveDriveImagePath_AcceptsPlainFileName()
    {
        var bundle = CreateBundleWithImageName("disk-0.qcow2", out _);
        Directory.CreateDirectory(bundle.DataDirectory);
        File.WriteAllText(Path.Combine(bundle.DataDirectory, "disk-0.qcow2"), "q");

        Assert.EndsWith("disk-0.qcow2", bundle.ResolveDriveImagePath(bundle.Configuration.Drives[0]));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class UtmBundleCloneTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("zutm-clone-tests-").FullName;

    private string BundlePath => Path.Combine(_tempRoot, "Original.utm");

    private UtmBundle CreatePopulatedBundle()
    {
        var configuration = new UtmConfiguration
        {
            Information = new UtmInformation { Name = "Original", Uuid = new Guid("6f0f0c6a-8dc0-47d1-a381-c0abf67a8c8d") },
            Drives =
            [
                new UtmDrive { ImageName = "data.qcow2", ImageType = UtmValues.DriveImageType.Disk, Interface = UtmValues.DriveInterface.Virtio },
            ],
        };
        var bundle = UtmBundle.CreateNew(BundlePath, configuration);
        Directory.CreateDirectory(bundle.DataDirectory);
        File.WriteAllText(Path.Combine(bundle.DataDirectory, "data.qcow2"), "disk-bytes");
        File.WriteAllText(Path.Combine(bundle.DataDirectory, "efi_vars.fd"), "vars");
        bundle.Save();
        return bundle;
    }

    [Fact]
    public void Clone_CopiesConfigAndData_WithNewIdentity()
    {
        var original = CreatePopulatedBundle();

        var clone = original.Clone();

        Assert.NotEqual(original.BundlePath, clone.BundlePath);
        Assert.Equal("Original copy", clone.Configuration.Information.Name);
        Assert.NotEqual(original.Id, clone.Id);
        Assert.True(Directory.Exists(clone.DataDirectory));

        var clonedDisk = clone.ResolveDriveImagePath(clone.Configuration.Drives[0]);
        Assert.NotNull(clonedDisk);
        Assert.Equal("disk-bytes", File.ReadAllText(clonedDisk!));

        // Reload from disk: the clone is a fully valid, independent bundle.
        var reloaded = UtmBundle.Load(clone.BundlePath);
        Assert.Equal(clone.Id, reloaded.Id);

        // Original untouched.
        Assert.Equal("Original", original.Configuration.Information.Name);
        Assert.True(File.Exists(Path.Combine(original.DataDirectory, "data.qcow2")));
    }

    [Fact]
    public void Clone_OptionallyKeepsIdentity()
    {
        var original = CreatePopulatedBundle();

        var clone = original.Clone(rename: false);

        Assert.Equal("Original", clone.Configuration.Information.Name);
        Assert.Equal(original.Id, clone.Id);
    }

    [Fact]
    public void Clone_RefusesExistingTarget()
    {
        var original = CreatePopulatedBundle();
        var target = Path.Combine(_tempRoot, "Taken.utm");
        Directory.CreateDirectory(target);

        Assert.Throws<IOException>(() => original.Clone(target));
    }

    [Fact]
    public void Clone_DefaultTargetIsSiblingCopy()
    {
        var original = CreatePopulatedBundle();

        var clone = original.Clone();

        Assert.Equal(Path.Combine(_tempRoot, "Original copy.utm"), clone.BundlePath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
