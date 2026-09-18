// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Core.Plist;

namespace ZuTM.Core.Utm;

/// <summary>Root of a UTM QEMU configuration (config.plist, ConfigurationVersion 4).</summary>
public sealed record UtmConfiguration
{
    public const int CurrentVersion = 4;
    public const int OldestSupportedVersion = 4;

    /// <summary>Backend string; ZuTM only runs <see cref="UtmValues.Backend.Qemu"/> configurations.</summary>
    public string Backend { get; init; } = UtmValues.Backend.Qemu;

    public int ConfigurationVersion { get; init; } = CurrentVersion;

    public UtmInformation Information { get; init; } = new();

    public UtmSystem System { get; init; } = new();

    public UtmQemu Qemu { get; init; } = new();

    public UtmInput Input { get; init; } = new();

    public UtmSharing Sharing { get; init; } = new();

    public IReadOnlyList<UtmDisplay> Displays { get; init; } = [];

    public IReadOnlyList<UtmDrive> Drives { get; init; } = [];

    public IReadOnlyList<UtmNetwork> Networks { get; init; } = [];

    public IReadOnlyList<UtmSerial> Serials { get; init; } = [];

    public IReadOnlyList<UtmSound> Sounds { get; init; } = [];

    /// <summary>Root-level keys ZuTM does not understand; re-emitted verbatim.</summary>
    public IReadOnlyDictionary<string, PlistNode> UnknownKeys { get; init; } =
        new Dictionary<string, PlistNode>();

    public static UtmConfiguration FromPlist(PlistDictionary source)
    {
        var backend = source.GetString("Backend", UtmValues.Backend.Unknown);
        if (backend == UtmValues.Backend.Apple)
        {
            throw new UtmConfigurationException(
                "This VM uses the Apple backend, which is macOS-only and cannot run on Windows.");
        }

        if (backend != UtmValues.Backend.Qemu)
        {
            throw new UtmConfigurationException(
                $"Unsupported backend '{backend}' (legacy UTM configurations are not yet supported).");
        }

        var version = (int)source.GetInteger("ConfigurationVersion", 0);
        if (version < OldestSupportedVersion)
        {
            throw new UtmConfigurationException(
                $"Configuration version {version} is too old; UTM v4+ bundles are required.");
        }

        if (version > CurrentVersion)
        {
            throw new UtmConfigurationException(
                $"Configuration version {version} is newer than supported ({CurrentVersion}); upgrade ZuTM.");
        }

        return new UtmConfiguration
        {
            Backend = backend,
            ConfigurationVersion = version,
            Information = Section(source, "Information", UtmInformation.FromPlist),
            System = Section(source, "System", UtmSystem.FromPlist),
            Qemu = Section(source, "QEMU", UtmQemu.FromPlist),
            Input = Section(source, "Input", UtmInput.FromPlist),
            Sharing = Section(source, "Sharing", UtmSharing.FromPlist),
            Displays = SectionList(source, "Display", UtmDisplay.FromPlist),
            Drives = SectionList(source, "Drive", UtmDrive.FromPlist),
            Networks = SectionList(source, "Network", UtmNetwork.FromPlist),
            Serials = SectionList(source, "Serial", UtmSerial.FromPlist),
            Sounds = SectionList(source, "Sound", UtmSound.FromPlist),
            UnknownKeys = UtmSectionHelper.CaptureUnknown(source, RootKnownKeys),
        };
    }

    private static readonly HashSet<string> RootKnownKeys =
    [
        "Backend", "ConfigurationVersion", "Information", "System", "QEMU",
        "Input", "Sharing", "Display", "Drive", "Network", "Serial", "Sound",
    ];

    private static T Section<T>(PlistDictionary source, string key, Func<PlistDictionary, T> parse)
    {
        if (source.GetDictionary(key) is not { } section)
        {
            throw new UtmConfigurationException($"Missing required '{key}' section in config.plist.");
        }

        return parse(section);
    }

    private static T[] SectionList<T>(PlistDictionary source, string key, Func<PlistDictionary, T> parse)
    {
        if (source.GetArray(key) is not { } array)
        {
            return [];
        }

        return array.OfType<PlistDictionary>().Select(parse).ToArray();
    }

    public PlistDictionary ToPlist()
    {
        var displays = new PlistArray();
        foreach (var display in Displays)
        {
            displays.Add(display.ToPlist());
        }

        var drives = new PlistArray();
        foreach (var drive in Drives)
        {
            drives.Add(drive.ToPlist());
        }

        var networks = new PlistArray();
        foreach (var network in Networks)
        {
            networks.Add(network.ToPlist());
        }

        var serials = new PlistArray();
        foreach (var serial in Serials)
        {
            serials.Add(serial.ToPlist());
        }

        var sounds = new PlistArray();
        foreach (var sound in Sounds)
        {
            sounds.Add(sound.ToPlist());
        }

        var dict = new PlistDictionary
        {
            ["Backend"] = new PlistString(Backend),
            ["ConfigurationVersion"] = new PlistInteger(ConfigurationVersion),
            ["Information"] = Information.ToPlist(),
            ["System"] = System.ToPlist(),
            ["QEMU"] = Qemu.ToPlist(),
            ["Input"] = Input.ToPlist(),
            ["Sharing"] = Sharing.ToPlist(),
            ["Display"] = displays,
            ["Drive"] = drives,
            ["Network"] = networks,
            ["Serial"] = serials,
            ["Sound"] = sounds,
        };
        UtmSectionHelper.EmitUnknown(dict, UnknownKeys);
        return dict;
    }
}

/// <summary>Thrown when a .utm configuration cannot be loaded or is unsupported.</summary>
public sealed class UtmConfigurationException(string message) : Exception(message);
