// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Core.Plist;
using ZuTM.Core.Utm;
using Xunit;

namespace ZuTM.Core.Tests.Utm;

public sealed class UtmBundleTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("zutm-bundle-tests-").FullName;

    private string BundlePath(string name = "Test.utm") => Path.Combine(_tempRoot, name);

    private void WriteMinimalBundle(string bundlePath, bool useLegacyImages = false)
    {
        Directory.CreateDirectory(bundlePath);
        var dataDirectory = Path.Combine(bundlePath, useLegacyImages ? "Images" : "Data");
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(dataDirectory, "disk-0.qcow2"), "fake qcow2");

        var config = new UtmConfiguration
        {
            Information = new UtmInformation { Name = "Test", Uuid = new Guid("6f0f0c6a-8dc0-47d1-a381-c0abf67a8c8d") },
            Drives =
            [
                new UtmDrive
                {
                    ImageName = "disk-0.qcow2",
                    ImageType = UtmValues.DriveImageType.Disk,
                    Interface = UtmValues.DriveInterface.Virtio,
                    Identifier = "11111111-2222-3333-4444-555555555555",
                },
            ],
        };
        PlistDocument.WriteFile(
            Path.Combine(bundlePath, "config.plist"),
            config.ToPlist(),
            PlistFormat.Xml);
    }

    [Fact]
    public void Loads_DataDirectory_Bundle()
    {
        WriteMinimalBundle(BundlePath());

        var bundle = UtmBundle.Load(BundlePath());

        Assert.Equal("Test", bundle.Name);
        Assert.Equal(new Guid("6f0f0c6a-8dc0-47d1-a381-c0abf67a8c8d"), bundle.Id);
        Assert.Equal("disk-0.qcow2", bundle.ListDataFiles()[0]);
        Assert.EndsWith(Path.Combine("Test.utm", "Data"), bundle.DataDirectory);
    }

    [Fact]
    public void Loads_LegacyImagesDirectory_Bundle()
    {
        WriteMinimalBundle(BundlePath("Legacy.utm"), useLegacyImages: true);

        var bundle = UtmBundle.Load(BundlePath("Legacy.utm"));

        Assert.EndsWith(Path.Combine("Legacy.utm", "Images"), bundle.DataDirectory);
        var drivePath = bundle.ResolveDriveImagePath(bundle.Configuration.Drives[0]);
        Assert.EndsWith("disk-0.qcow2", drivePath);
    }

    [Fact]
    public void ResolveDriveImagePath_ReturnsNull_ForMissingExternalDrive()
    {
        WriteMinimalBundle(BundlePath());
        var bundle = UtmBundle.Load(BundlePath());

        var external = new UtmDrive { Identifier = "ext-1", ImageType = UtmValues.DriveImageType.Disk };
        Assert.Null(bundle.ResolveDriveImagePath(external));
    }

    [Fact]
    public void ResolveDriveImagePath_UsesExternalPathFromState()
    {
        WriteMinimalBundle(BundlePath());
        var bundle = UtmBundle.Load(BundlePath());
        var external = new UtmDrive { Identifier = "ext-1", ImageType = UtmValues.DriveImageType.Disk };
        var externalPath = Path.Combine(_tempRoot, "outside.qcow2");
        File.WriteAllText(externalPath, "x");

        bundle.State = bundle.State with
        {
            ExternalDrivePaths = new Dictionary<string, string> { ["ext-1"] = externalPath },
        };

        Assert.Equal(externalPath, bundle.ResolveDriveImagePath(external));
    }

    [Fact]
    public void Save_RewritesConfig_AndRoundTripsThroughUtm()
    {
        WriteMinimalBundle(BundlePath());
        var bundle = UtmBundle.Load(BundlePath());

        bundle.Configuration = bundle.Configuration with
        {
            System = bundle.Configuration.System with { MemorySizeMib = 8192 },
        };
        bundle.Save();

        var reloaded = UtmBundle.Load(BundlePath());
        Assert.Equal(8192, reloaded.Configuration.System.MemorySizeMib);
    }

    [Fact]
    public void Save_IsAtomic_NoTempFileLeftBehind()
    {
        WriteMinimalBundle(BundlePath());
        var bundle = UtmBundle.Load(BundlePath());
        bundle.Save();

        Assert.False(File.Exists(Path.Combine(BundlePath(), "config.plist.tmp")));
        Assert.True(File.Exists(Path.Combine(BundlePath(), "config.plist")));
    }

    [Fact]
    public void Save_CreatesDataDirectory_WhenMissing()
    {
        var path = BundlePath("Fresh.utm");
        Directory.CreateDirectory(path);
        PlistDocument.WriteFile(Path.Combine(path, "config.plist"),
            new UtmConfiguration { Information = new UtmInformation { Name = "Fresh" } }.ToPlist());

        var bundle = UtmBundle.Load(path);
        bundle.Save();

        Assert.True(Directory.Exists(Path.Combine(path, "Data")));
    }

    [Fact]
    public void State_SurvivesReload_AndIsKeptOutOfConfigPlist()
    {
        WriteMinimalBundle(BundlePath());
        var bundle = UtmBundle.Load(BundlePath());
        bundle.State = bundle.State with { LastStartedUtc = DateTimeOffset.UtcNow, Window = new ZutmWindowState { X = 10, Y = 20, Width = 800, Height = 600 } };
        bundle.Save();

        var reloaded = UtmBundle.Load(BundlePath());

        Assert.Equal(bundle.State.LastStartedUtc, reloaded.State.LastStartedUtc);
        Assert.Equal(800, reloaded.State.Window!.Width);

        // config.plist stays byte-identical to what UTM expects: no ZuTM keys leak in.
        var configText = File.ReadAllText(Path.Combine(BundlePath(), "config.plist"));
        Assert.DoesNotContain("zutm", configText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImportDriveImage_CopiesIntoBundle()
    {
        WriteMinimalBundle(BundlePath());
        var bundle = UtmBundle.Load(BundlePath());
        var source = Path.Combine(_tempRoot, "imported.img");
        File.WriteAllText(source, "disk!");

        var drive = bundle.ImportDriveImage(source);

        Assert.Equal("imported.img", drive.ImageName);
        Assert.True(File.Exists(Path.Combine(bundle.DataDirectory, "imported.img")));
        Assert.Equal(new FileInfo(source).Length, new FileInfo(Path.Combine(bundle.DataDirectory, "imported.img")).Length);
    }

    [Fact]
    public void ImportDriveImage_AvoidsCollisions()
    {
        WriteMinimalBundle(BundlePath());
        var bundle = UtmBundle.Load(BundlePath());
        var source = Path.Combine(_tempRoot, "collide.img");
        File.WriteAllText(source, "a");

        bundle.ImportDriveImage(source);
        var second = bundle.ImportDriveImage(source);

        Assert.Equal("collide-1.img", second.ImageName);
    }

    [Fact]
    public void DeleteDriveImage_RemovesOnlyBundledImages()
    {
        WriteMinimalBundle(BundlePath());
        var bundle = UtmBundle.Load(BundlePath());

        bundle.DeleteDriveImage(bundle.Configuration.Drives[0]);
        Assert.False(File.Exists(Path.Combine(bundle.DataDirectory, "disk-0.qcow2")));

        var external = new UtmDrive { Identifier = "e", ImageType = UtmValues.DriveImageType.Disk };
        bundle.DeleteDriveImage(external); // must not throw
    }

    [Fact]
    public void FindBundles_ListsUtmDirectoriesOnly()
    {
        WriteMinimalBundle(BundlePath("A.utm"));
        WriteMinimalBundle(BundlePath("B.utm"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "not-a-vm"));

        var bundles = UtmBundle.FindBundles(_tempRoot);

        Assert.Equal(["A.utm", "B.utm"], bundles.Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Load_ThrowsForNonUtmDirectory()
    {
        var path = Path.Combine(_tempRoot, "plain.dir");
        Directory.CreateDirectory(path);
        Assert.Throws<UtmConfigurationException>(() => UtmBundle.Load(path));
    }

    [Fact]
    public void Load_ThrowsForMissingConfig()
    {
        var path = BundlePath("Empty.utm");
        Directory.CreateDirectory(path);
        Assert.Throws<FileNotFoundException>(() => UtmBundle.Load(path));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup best-effort on Windows.
        }
    }
}
