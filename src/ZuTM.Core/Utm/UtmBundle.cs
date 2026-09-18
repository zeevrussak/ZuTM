// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Core.Plist;

namespace ZuTM.Core.Utm;

/// <summary>Well-known file names inside a .utm bundle directory.</summary>
public static class UtmBundleFiles
{
    public const string BundleExtension = ".utm";
    public const string ConfigPlist = "config.plist";
    public const string DataDirectory = "Data";
    public const string LegacyImagesDirectory = "Images"; // UTM ≤ 3
    public const string EfiVariables = "efi_vars.fd";
    public const string TpmData = "tpmdata";
    public const string VmState = "vmstate";
    public const string DebugLog = "debug.log";
}

/// <summary>An open .utm bundle: configuration plus its Data/ payloads.</summary>
public sealed class UtmBundle
{
    /// <summary>Absolute path of the bundle directory (…\My VM.utm).</summary>
    public string BundlePath { get; }

    public UtmConfiguration Configuration { get; set; }

    /// <summary>ZuTM-private state (never touches config.plist).</summary>
    public ZutmState State { get; set; } = new();

    /// <summary>Data directory chosen when loading; legacy bundles keep using Images/ until migrated by UTM.</summary>
    public string DataDirectory { get; private set; }

    private UtmBundle(string bundlePath, UtmConfiguration configuration, string dataDirectory)
    {
        BundlePath = bundlePath;
        Configuration = configuration;
        DataDirectory = dataDirectory;
    }

    /// <summary>Creates a wrapper for a new bundle directory (created on first Save).</summary>
    public static UtmBundle CreateNew(string bundlePath, UtmConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentNullException.ThrowIfNull(configuration);
        var fullPath = Path.GetFullPath(bundlePath);
        return new UtmBundle(fullPath, configuration, Path.Combine(fullPath, UtmBundleFiles.DataDirectory));
    }

    public string Name => Path.GetFileName(BundlePath.TrimEnd(Path.DirectorySeparatorChar))[..^UtmBundleFiles.BundleExtension.Length];

    public Guid Id => Configuration.Information.Uuid;

    // -- Loading ------------------------------------------------------------------

    /// <summary>Loads a .utm bundle directory. Throws <see cref="UtmConfigurationException"/> for unsupported configs.</summary>
    public static UtmBundle Load(string bundlePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        if (!Directory.Exists(bundlePath))
        {
            throw new DirectoryNotFoundException($"Bundle directory not found: {bundlePath}");
        }

        if (!bundlePath.EndsWith(UtmBundleFiles.BundleExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new UtmConfigurationException($"Not a .utm bundle: {bundlePath}");
        }

        var configPath = Path.Combine(bundlePath, UtmBundleFiles.ConfigPlist);
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("Bundle has no config.plist.", configPath);
        }

        var configuration = UtmConfiguration.FromPlist(PlistDocument.ParseDictionary(File.ReadAllBytes(configPath)));

        var dataDirectory = Path.Combine(bundlePath, UtmBundleFiles.DataDirectory);
        if (!Directory.Exists(dataDirectory))
        {
            var legacy = Path.Combine(bundlePath, UtmBundleFiles.LegacyImagesDirectory);
            if (Directory.Exists(legacy))
            {
                dataDirectory = legacy;
            }
        }

        var bundle = new UtmBundle(Path.GetFullPath(bundlePath), configuration, dataDirectory)
        {
            State = ZutmState.TryLoad(Path.Combine(bundlePath, ZutmState.FileName)) ?? new ZutmState(),
        };
        return bundle;
    }

    /// <summary>Enumerates .utm bundles directly under a folder.</summary>
    public static IReadOnlyList<string> FindBundles(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateDirectories(folder, $"*{UtmBundleFiles.BundleExtension}")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // -- Saving -------------------------------------------------------------------

    /// <summary>
    /// Persists config.plist (XML plist, matching UTM) and zutm-state.json.
    /// The write is transactional: config is staged then moved into place so
    /// a crash mid-save never truncates the file UTM will read.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(BundlePath);
        Directory.CreateDirectory(Path.Combine(BundlePath, UtmBundleFiles.DataDirectory));

        var configPath = Path.Combine(BundlePath, UtmBundleFiles.ConfigPlist);
        AtomicWriteAllBytes(configPath, PlistDocument.Write(Configuration.ToPlist(), PlistFormat.Xml));

        State.Save(Path.Combine(BundlePath, ZutmState.FileName));
    }

    private static void AtomicWriteAllBytes(string path, byte[] bytes)
    {
        var staging = path + ".tmp";
        File.WriteAllBytes(staging, bytes);
        if (File.Exists(path))
        {
            File.Replace(staging, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(staging, path);
        }
    }

    // -- Drive image helpers --------------------------------------------------------

    /// <summary>Absolute path of a drive image: bundle Data/&lt;ImageName&gt;, or the external path from ZuTM state.</summary>
    public string? ResolveDriveImagePath(UtmDrive drive)
    {
        if (!drive.IsExternal)
        {
            var bundled = Path.Combine(DataDirectory, drive.ImageName!);
            return File.Exists(bundled) ? bundled : null;
        }

        return State.ExternalDrivePaths.TryGetValue(drive.Identifier, out var external) && File.Exists(external)
            ? external
            : null;
    }

    /// <summary>Copies a disk image into the bundle's Data/ directory and returns the new drive entry.</summary>
    public UtmDrive ImportDriveImage(string sourceImagePath, bool isReadOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceImagePath);
        if (!File.Exists(sourceImagePath))
        {
            throw new FileNotFoundException("Drive image not found.", sourceImagePath);
        }

        Directory.CreateDirectory(DataDirectory);
        var fileName = UniqueDataFileName(Path.GetFileName(sourceImagePath));
        File.Copy(sourceImagePath, Path.Combine(DataDirectory, fileName));

        return new UtmDrive
        {
            ImageName = fileName,
            ImageType = UtmValues.DriveImageType.Disk,
            Interface = UtmValues.DriveInterface.Virtio,
            IsReadOnly = isReadOnly,
        };
    }

    /// <summary>Removes a drive's bundled image file from Data/ (external images are left in place).</summary>
    public void DeleteDriveImage(UtmDrive drive)
    {
        if (drive.IsExternal)
        {
            return;
        }

        var path = Path.Combine(DataDirectory, drive.ImageName!);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string UniqueDataFileName(string desiredName)
    {
        var candidate = desiredName;
        for (var attempt = 1; File.Exists(Path.Combine(DataDirectory, candidate)); attempt++)
        {
            candidate = $"{Path.GetFileNameWithoutExtension(desiredName)}-{attempt}{Path.GetExtension(desiredName)}";
        }

        return candidate;
    }

    /// <summary>All payload files currently in the bundle's data directory.</summary>
    public IReadOnlyList<string> ListDataFiles() =>
        Directory.Exists(DataDirectory)
            ? Directory.EnumerateFiles(DataDirectory).Select(f => Path.GetFileName(f)!).ToArray()
            : [];
}
