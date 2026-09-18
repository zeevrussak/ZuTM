// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
// Clean-room model of the UTM v4 QEMU configuration sections.
// Every section preserves keys it does not understand via UnknownKeys.

using ZuTM.Core.Plist;

namespace ZuTM.Core.Utm;

/// <summary>Basic information about a VM (<c>Information</c> section).</summary>
public sealed record UtmInformation
{
    public string Name { get; init; } = "";

    /// <summary>UPPERCASE UUID string with dashes (UTM stores UUID().uuidString, which is uppercase).</summary>
    public Guid Uuid { get; init; } = Guid.NewGuid();

    public string Icon { get; init; } = "";

    public bool IsIconCustom { get; init; }

    public string Notes { get; init; } = "";

    public IReadOnlyDictionary<string, PlistNode> UnknownKeys { get; init; } =
        new Dictionary<string, PlistNode>();

    private static readonly HashSet<string> Known =
    [
        "Name", "Icon", "IconCustom", "Notes", "UUID",
    ];

    public static UtmInformation FromPlist(PlistDictionary source) => new()
    {
        Name = source.GetString("Name", ""),
        Uuid = ParseUuid(source.GetString("UUID")),
        Icon = source.GetString("Icon", ""),
        IsIconCustom = source.GetBoolean("IconCustom", false),
        Notes = source.GetString("Notes", ""),
        UnknownKeys = UtmSectionHelper.CaptureUnknown(source, Known),
    };

    public PlistDictionary ToPlist()
    {
        var dict = new PlistDictionary
        {
            ["Name"] = new PlistString(Name),
            ["UUID"] = new PlistString(Uuid.ToString("D").ToUpperInvariant()),
            ["Icon"] = new PlistString(Icon),
            ["IconCustom"] = new PlistBoolean(IsIconCustom),
            ["Notes"] = new PlistString(Notes),
        };
        UtmSectionHelper.EmitUnknown(dict, UnknownKeys);
        return dict;
    }

    private static Guid ParseUuid(string? value) =>
        Guid.TryParse(value, out var uuid) ? uuid : Guid.NewGuid();
}

/// <summary>System section: architecture, target machine, CPU, memory.</summary>
public sealed record UtmSystem
{
    /// <summary>QEMU architecture string, e.g. "x86_64", "aarch64". Raw string for lossless round-trip.</summary>
    public string Architecture { get; init; } = "x86_64";

    /// <summary>QEMU machine target, e.g. "q35".</summary>
    public string Target { get; init; } = "q35";

    /// <summary>QEMU CPU model, e.g. "default", "host", "max".</summary>
    public string Cpu { get; init; } = "default";

    public IReadOnlyList<string> CpuFlagsAdd { get; init; } = [];

    public IReadOnlyList<string> CpuFlagsRemove { get; init; } = [];

    /// <summary>Number of vCPUs; 0 = match host.</summary>
    public int CpuCount { get; init; }

    public bool ForceMulticore { get; init; }

    /// <summary>RAM in MiB.</summary>
    public int MemorySizeMib { get; init; } = 512;

    /// <summary>TCG JIT cache in MiB; 0 = default. (UTM field name is historical.)</summary>
    public int JitCacheSizeMib { get; init; }

    public IReadOnlyDictionary<string, PlistNode> UnknownKeys { get; init; } =
        new Dictionary<string, PlistNode>();

    private static readonly HashSet<string> Known =
    [
        "Architecture", "Target", "CPU", "CPUFlagsAdd", "CPUFlagsRemove",
        "CPUCount", "ForceMulticore", "MemorySize", "JITCacheSize",
    ];

    public static UtmSystem FromPlist(PlistDictionary source) => new()
    {
        Architecture = source.GetString("Architecture", "x86_64"),
        Target = source.GetString("Target", "q35"),
        Cpu = source.GetString("CPU", "default"),
        CpuFlagsAdd = StringList(source, "CPUFlagsAdd"),
        CpuFlagsRemove = StringList(source, "CPUFlagsRemove"),
        CpuCount = (int)source.GetInteger("CPUCount", 0),
        ForceMulticore = source.GetBoolean("ForceMulticore", false),
        MemorySizeMib = (int)source.GetInteger("MemorySize", 512),
        JitCacheSizeMib = (int)source.GetInteger("JITCacheSize", 0),
        UnknownKeys = UtmSectionHelper.CaptureUnknown(source, Known),
    };

    public PlistDictionary ToPlist()
    {
        var dict = new PlistDictionary
        {
            ["Architecture"] = new PlistString(Architecture),
            ["Target"] = new PlistString(Target),
            ["CPU"] = new PlistString(Cpu),
            ["CPUFlagsAdd"] = PlistFactory.Strings([.. CpuFlagsAdd]),
            ["CPUFlagsRemove"] = PlistFactory.Strings([.. CpuFlagsRemove]),
            ["CPUCount"] = new PlistInteger(CpuCount),
            ["ForceMulticore"] = new PlistBoolean(ForceMulticore),
            ["MemorySize"] = new PlistInteger(MemorySizeMib),
            ["JITCacheSize"] = new PlistInteger(JitCacheSizeMib),
        };
        UtmSectionHelper.EmitUnknown(dict, UnknownKeys);
        return dict;
    }

    internal static IReadOnlyList<string> StringList(PlistDictionary source, string key)
    {
        if (source.GetArray(key) is not { } array)
        {
            return [];
        }

        return array.OfType<PlistString>().Select(s => s.Value).ToArray();
    }
}

/// <summary>QEMU behavior tweaks (<c>QEMU</c> section).</summary>
public sealed record UtmQemu
{
    public bool HasDebugLog { get; init; }

    public bool HasUefiBoot { get; init; }

    public bool HasRngDevice { get; init; } = true;

    public bool HasBalloonDevice { get; init; } = true;

    public bool HasTpmDevice { get; init; }

    /// <summary>Use host hypervisor acceleration when available (WHPX on Windows, HVF on macOS).</summary>
    public bool HasHypervisor { get; init; } = true;

    public bool HasTso { get; init; }

    public bool HasRtcLocalTime { get; init; }

    public string MachinePropertyOverride { get; init; } = "";

    /// <summary>Extra arguments; each entry is an array of argv tokens (UTM: list of QEMUArgument lists).</summary>
    public IReadOnlyList<UtmQemuArgument> AdditionalArguments { get; init; } = [];

    public IReadOnlyDictionary<string, PlistNode> UnknownKeys { get; init; } =
        new Dictionary<string, PlistNode>();

    private static readonly HashSet<string> Known =
    [
        "DebugLog", "UEFIBoot", "RNGDevice", "BalloonDevice", "TPMDevice",
        "Hypervisor", "TSO", "RTCLocalTime", "MachinePropertyOverride",
        "AdditionalArguments",
    ];

    public static UtmQemu FromPlist(PlistDictionary source) => new()
    {
        HasDebugLog = source.GetBoolean("DebugLog", false),
        HasUefiBoot = source.GetBoolean("UEFIBoot", false),
        HasRngDevice = source.GetBoolean("RNGDevice", true),
        HasBalloonDevice = source.GetBoolean("BalloonDevice", true),
        HasTpmDevice = source.GetBoolean("TPMDevice", false),
        HasHypervisor = source.GetBoolean("Hypervisor", true),
        HasTso = source.GetBoolean("TSO", false),
        HasRtcLocalTime = source.GetBoolean("RTCLocalTime", false),
        MachinePropertyOverride = source.GetString("MachinePropertyOverride", ""),
        AdditionalArguments = ParseAdditionalArguments(source.GetArray("AdditionalArguments")),
        UnknownKeys = UtmSectionHelper.CaptureUnknown(source, Known),
    };

    public PlistDictionary ToPlist()
    {
        var additional = new PlistArray();
        foreach (var argument in AdditionalArguments)
        {
            additional.Add(argument.ToPlist());
        }

        var dict = new PlistDictionary
        {
            ["DebugLog"] = new PlistBoolean(HasDebugLog),
            ["UEFIBoot"] = new PlistBoolean(HasUefiBoot),
            ["RNGDevice"] = new PlistBoolean(HasRngDevice),
            ["BalloonDevice"] = new PlistBoolean(HasBalloonDevice),
            ["TPMDevice"] = new PlistBoolean(HasTpmDevice),
            ["Hypervisor"] = new PlistBoolean(HasHypervisor),
            ["TSO"] = new PlistBoolean(HasTso),
            ["RTCLocalTime"] = new PlistBoolean(HasRtcLocalTime),
            ["MachinePropertyOverride"] = new PlistString(MachinePropertyOverride),
            ["AdditionalArguments"] = additional,
        };
        UtmSectionHelper.EmitUnknown(dict, UnknownKeys);
        return dict;
    }

    /// <summary>
    /// UTM stores AdditionalArguments as an array of dicts shaped
    /// <c>{"qemuArgument": ["-flag", "value"]}</c>. ZuTM parses that shape into
    /// token lists and re-emits it identically; entries with any other shape
    /// are kept verbatim (<see cref="UtmQemuArgument.Raw"/>) for lossless round-trips.
    /// </summary>
    private static List<UtmQemuArgument> ParseAdditionalArguments(PlistArray? array)
    {
        if (array is null)
        {
            return [];
        }

        List<UtmQemuArgument> result = [];
        foreach (var entry in array)
        {
            if (entry is PlistDictionary { } dict && dict.GetArray("qemuArgument") is { } tokens)
            {
                result.Add(new UtmQemuArgument(tokens.OfType<PlistString>().Select(s => s.Value).ToArray()));
            }
            else
            {
                result.Add(new UtmQemuArgument(entry));
            }
        }

        return result;
    }
}

/// <summary>A single AdditionalArguments entry: parsed tokens, or an unrecognized raw node preserved verbatim.</summary>
public sealed record UtmQemuArgument
{
    /// <summary>argv tokens; null when the entry had an unknown shape.</summary>
    public IReadOnlyList<string>? Tokens { get; }

    /// <summary>Original plist node for entries ZuTM does not understand.</summary>
    public PlistNode? Raw { get; }

    public UtmQemuArgument(IReadOnlyList<string> tokens)
    {
        Tokens = tokens;
    }

    public UtmQemuArgument(PlistNode raw)
    {
        Raw = raw;
    }

    public PlistNode ToPlist() => Raw ?? PlistFactory.Dict(("qemuArgument", PlistFactory.Strings([.. Tokens!])));
}

/// <summary>Input settings (<c>Input</c> section).</summary>
public sealed record UtmInput
{
    /// <summary>One of <see cref="UtmValues.UsbBusSupport"/> (raw string).</summary>
    public string UsbBusSupport { get; init; } = UtmValues.UsbBusSupport.Default;

    public bool HasUsbSharing { get; init; }

    public int MaximumUsbShare { get; init; } = 3;

    public IReadOnlyDictionary<string, PlistNode> UnknownKeys { get; init; } =
        new Dictionary<string, PlistNode>();

    private static readonly HashSet<string> Known =
    [
        "UsbBusSupport", "UsbSharing", "MaximumUsbShare",
    ];

    public static UtmInput FromPlist(PlistDictionary source) => new()
    {
        UsbBusSupport = source.GetString("UsbBusSupport", UtmValues.UsbBusSupport.Default),
        HasUsbSharing = source.GetBoolean("UsbSharing", false),
        MaximumUsbShare = (int)source.GetInteger("MaximumUsbShare", 3),
        UnknownKeys = UtmSectionHelper.CaptureUnknown(source, Known),
    };

    public PlistDictionary ToPlist()
    {
        var dict = new PlistDictionary
        {
            ["UsbBusSupport"] = new PlistString(UsbBusSupport),
            ["UsbSharing"] = new PlistBoolean(HasUsbSharing),
            ["MaximumUsbShare"] = new PlistInteger(MaximumUsbShare),
        };
        UtmSectionHelper.EmitUnknown(dict, UnknownKeys);
        return dict;
    }
}

/// <summary>Sharing settings (<c>Sharing</c> section).</summary>
public sealed record UtmSharing
{
    /// <summary>One of <see cref="UtmValues.DirectoryShareMode"/> (raw string).</summary>
    public string DirectoryShareMode { get; init; } = UtmValues.DirectoryShareMode.None;

    public bool IsDirectoryShareReadOnly { get; init; }

    public bool HasClipboardSharing { get; init; }

    public IReadOnlyDictionary<string, PlistNode> UnknownKeys { get; init; } =
        new Dictionary<string, PlistNode>();

    private static readonly HashSet<string> Known =
    [
        "DirectoryShareMode", "DirectoryShareReadOnly", "ClipboardSharing",
    ];

    public static UtmSharing FromPlist(PlistDictionary source) => new()
    {
        DirectoryShareMode = source.GetString("DirectoryShareMode", UtmValues.DirectoryShareMode.None),
        IsDirectoryShareReadOnly = source.GetBoolean("DirectoryShareReadOnly", false),
        HasClipboardSharing = source.GetBoolean("ClipboardSharing", false),
        UnknownKeys = UtmSectionHelper.CaptureUnknown(source, Known),
    };

    public PlistDictionary ToPlist()
    {
        var dict = new PlistDictionary
        {
            ["DirectoryShareMode"] = new PlistString(DirectoryShareMode),
            ["DirectoryShareReadOnly"] = new PlistBoolean(IsDirectoryShareReadOnly),
            ["ClipboardSharing"] = new PlistBoolean(HasClipboardSharing),
        };
        UtmSectionHelper.EmitUnknown(dict, UnknownKeys);
        return dict;
    }
}

/// <summary>Display card entry (<c>Display[]</c>).</summary>
public sealed record UtmDisplay
{
    /// <summary>Display hardware, e.g. "virtio-gpu-gl", "QXL", "bochs-drm" (raw string).</summary>
    public string Hardware { get; init; } = "";

    public int VgaRamMib { get; init; } = 128;

    public bool IsDynamicResolution { get; init; }

    public string UpscalingFilter { get; init; } = UtmValues.ScalingFilter.Linear;

    public string DownscalingFilter { get; init; } = UtmValues.ScalingFilter.Linear;

    public bool IsNativeResolution { get; init; }

    public IReadOnlyDictionary<string, PlistNode> UnknownKeys { get; init; } =
        new Dictionary<string, PlistNode>();

    private static readonly HashSet<string> Known =
    [
        "Hardware", "VgaRamMib", "DynamicResolution", "UpscalingFilter",
        "DownscalingFilter", "NativeResolution",
    ];

    public static UtmDisplay FromPlist(PlistDictionary source) => new()
    {
        Hardware = source.GetString("Hardware", ""),
        VgaRamMib = (int)source.GetInteger("VgaRamMib", 128),
        IsDynamicResolution = source.GetBoolean("DynamicResolution", false),
        UpscalingFilter = source.GetString("UpscalingFilter", UtmValues.ScalingFilter.Linear),
        DownscalingFilter = source.GetString("DownscalingFilter", UtmValues.ScalingFilter.Linear),
        IsNativeResolution = source.GetBoolean("NativeResolution", false),
        UnknownKeys = UtmSectionHelper.CaptureUnknown(source, Known),
    };

    public PlistDictionary ToPlist()
    {
        var dict = new PlistDictionary
        {
            ["Hardware"] = new PlistString(Hardware),
            ["VgaRamMib"] = new PlistInteger(VgaRamMib),
            ["DynamicResolution"] = new PlistBoolean(IsDynamicResolution),
            ["UpscalingFilter"] = new PlistString(UpscalingFilter),
            ["DownscalingFilter"] = new PlistString(DownscalingFilter),
            ["NativeResolution"] = new PlistBoolean(IsNativeResolution),
        };
        UtmSectionHelper.EmitUnknown(dict, UnknownKeys);
        return dict;
    }
}

/// <summary>Disk entry (<c>Drive[]</c>).</summary>
public sealed record UtmDrive
{
    /// <summary>File name inside the bundle's Data/ directory; null ⇒ external image.</summary>
    public string? ImageName { get; init; }

    /// <summary>One of <see cref="UtmValues.DriveImageType"/> (raw string).</summary>
    public string ImageType { get; init; } = UtmValues.DriveImageType.None;

    /// <summary>One of <see cref="UtmValues.DriveInterface"/> (raw string).</summary>
    public string Interface { get; init; } = UtmValues.DriveInterface.None;

    public int InterfaceVersion { get; init; } = 1;

    /// <summary>Stable drive identifier (UUID string).</summary>
    public string Identifier { get; init; } = Guid.NewGuid().ToString("D").ToUpperInvariant();

    public bool IsReadOnly { get; init; }

    public IReadOnlyDictionary<string, PlistNode> UnknownKeys { get; init; } =
        new Dictionary<string, PlistNode>();

    private static readonly HashSet<string> Known =
    [
        "ImageName", "ImageType", "Interface", "InterfaceVersion", "Identifier", "ReadOnly",
    ];

    public bool IsExternal => ImageName is null;

    public static UtmDrive FromPlist(PlistDictionary source) => new()
    {
        ImageName = source.GetString("ImageName"),
        ImageType = source.GetString("ImageType", UtmValues.DriveImageType.None),
        Interface = source.GetString("Interface", UtmValues.DriveInterface.None),
        InterfaceVersion = (int)source.GetInteger("InterfaceVersion", 1),
        Identifier = source.GetString("Identifier", Guid.NewGuid().ToString("D").ToUpperInvariant()),
        IsReadOnly = source.GetBoolean("ReadOnly", source.GetString("ImageName") is null),
        UnknownKeys = UtmSectionHelper.CaptureUnknown(source, Known),
    };

    public PlistDictionary ToPlist()
    {
        var dict = new PlistDictionary
        {
            ["ImageType"] = new PlistString(ImageType),
            ["Interface"] = new PlistString(Interface),
            ["InterfaceVersion"] = new PlistInteger(InterfaceVersion),
            ["Identifier"] = new PlistString(Identifier),
            ["ReadOnly"] = new PlistBoolean(IsReadOnly),
        };
        if (ImageName is not null)
        {
            dict["ImageName"] = new PlistString(ImageName);
        }

        UtmSectionHelper.EmitUnknown(dict, UnknownKeys);
        return dict;
    }
}

/// <summary>Port forwarding rule (<c>Network[].PortForward[]</c>).</summary>
public sealed record UtmPortForward
{
    /// <summary>TCP or UDP (raw string).</summary>
    public string Protocol { get; init; } = UtmValues.PortForwardProtocol.Tcp;

    public string HostAddress { get; init; } = "";

    public int HostPort { get; init; }

    public string GuestAddress { get; init; } = "";

    public int GuestPort { get; init; }

    public IReadOnlyDictionary<string, PlistNode> UnknownKeys { get; init; } =
        new Dictionary<string, PlistNode>();

    private static readonly HashSet<string> Known =
    [
        "protocol", "hostAddress", "hostPort", "guestAddress", "guestPort",
    ];

    public static UtmPortForward FromPlist(PlistDictionary source) => new()
    {
        Protocol = source.GetString("protocol", UtmValues.PortForwardProtocol.Tcp),
        HostAddress = source.GetString("hostAddress", ""),
        HostPort = (int)source.GetInteger("hostPort", 0),
        GuestAddress = source.GetString("guestAddress", ""),
        GuestPort = (int)source.GetInteger("guestPort", 0),
        UnknownKeys = UtmSectionHelper.CaptureUnknown(source, Known),
    };

    public PlistDictionary ToPlist()
    {
        var dict = new PlistDictionary
        {
            ["protocol"] = new PlistString(Protocol),
            ["hostAddress"] = new PlistString(HostAddress),
            ["hostPort"] = new PlistInteger(HostPort),
            ["guestAddress"] = new PlistString(GuestAddress),
            ["guestPort"] = new PlistInteger(GuestPort),
        };
        UtmSectionHelper.EmitUnknown(dict, UnknownKeys);
        return dict;
    }
}

/// <summary>Network adapter entry (<c>Network[]</c>).</summary>
public sealed record UtmNetwork
{
    /// <summary>One of <see cref="UtmValues.NetworkMode"/> (raw string).</summary>
    public string Mode { get; init; } = UtmValues.NetworkMode.Shared;

    /// <summary>NIC model, e.g. "virtio-net-pci", "e1000" (raw string).</summary>
    public string Hardware { get; init; } = "";

    public string MacAddress { get; init; } = "";

    public bool IsIsolateFromHost { get; init; }

    public IReadOnlyList<UtmPortForward> PortForward { get; init; } = [];

    public string BridgeInterface { get; init; } = "";

    public IReadOnlyDictionary<string, PlistNode> UnknownKeys { get; init; } =
        new Dictionary<string, PlistNode>();

    private static readonly HashSet<string> Known = ["Mode", "Hardware", "MacAddress", "IsolateFromHost", "PortForward", "BridgeInterface"];

    public static UtmNetwork FromPlist(PlistDictionary source) => new()
    {
        Mode = source.GetString("Mode", UtmValues.NetworkMode.Shared),
        Hardware = source.GetString("Hardware", ""),
        MacAddress = source.GetString("MacAddress", ""),
        IsIsolateFromHost = source.GetBoolean("IsolateFromHost", false),
        PortForward = ParsePortForwards(source.GetArray("PortForward")),
        BridgeInterface = source.GetString("BridgeInterface", ""),
        UnknownKeys = UtmSectionHelper.CaptureUnknown(source, Known),
    };

    public PlistDictionary ToPlist()
    {
        var forwards = new PlistArray();
        foreach (var forward in PortForward)
        {
            forwards.Add(forward.ToPlist());
        }

        var dict = new PlistDictionary
        {
            ["Mode"] = new PlistString(Mode),
            ["Hardware"] = new PlistString(Hardware),
            ["MacAddress"] = new PlistString(MacAddress),
            ["IsolateFromHost"] = new PlistBoolean(IsIsolateFromHost),
            ["PortForward"] = forwards,
            ["BridgeInterface"] = new PlistString(BridgeInterface),
        };
        UtmSectionHelper.EmitUnknown(dict, UnknownKeys);
        return dict;
    }

    private static UtmPortForward[] ParsePortForwards(PlistArray? array)
    {
        if (array is null)
        {
            return [];
        }

        return array.OfType<PlistDictionary>().Select(UtmPortForward.FromPlist).ToArray();
    }
}

/// <summary>Serial port entry (<c>Serial[]</c>).</summary>
public sealed record UtmSerial
{
    /// <summary>One of <see cref="UtmValues.SerialMode"/> (raw string).</summary>
    public string Mode { get; init; } = UtmValues.SerialMode.Terminal;

    /// <summary>One of <see cref="UtmValues.SerialTarget"/> (raw string).</summary>
    public string Target { get; init; } = UtmValues.SerialTarget.Auto;

    /// <summary>Serial hardware, e.g. "isa-serial" (raw string).</summary>
    public string Hardware { get; init; } = "";

    /// <summary>Terminal appearance settings; opaque to ZuTM, preserved verbatim.</summary>
    public PlistDictionary? Terminal { get; init; }

    public string TcpHostAddress { get; init; } = "";

    public int TcpPort { get; init; }

    public bool IsWaitForConnection { get; init; }

    public bool IsRemoteConnectionAllowed { get; init; }

    public IReadOnlyDictionary<string, PlistNode> UnknownKeys { get; init; } =
        new Dictionary<string, PlistNode>();

    private static readonly HashSet<string> Known =
    [
        "Mode", "Target", "Terminal", "Hardware", "TcpHostAddress", "TcpPort",
        "WaitForConnection", "RemoteConnectionAllowed",
    ];

    public static UtmSerial FromPlist(PlistDictionary source) => new()
    {
        Mode = source.GetString("Mode", UtmValues.SerialMode.Terminal),
        Target = source.GetString("Target", UtmValues.SerialTarget.Auto),
        Hardware = source.GetString("Hardware", ""),
        Terminal = source.GetDictionary("Terminal"),
        TcpHostAddress = source.GetString("TcpHostAddress", ""),
        TcpPort = (int)source.GetInteger("TcpPort", 0),
        IsWaitForConnection = source.GetBoolean("WaitForConnection", false),
        IsRemoteConnectionAllowed = source.GetBoolean("RemoteConnectionAllowed", false),
        UnknownKeys = UtmSectionHelper.CaptureUnknown(source, Known),
    };

    public PlistDictionary ToPlist()
    {
        var dict = new PlistDictionary
        {
            ["Mode"] = new PlistString(Mode),
            ["Target"] = new PlistString(Target),
            ["Hardware"] = new PlistString(Hardware),
            ["TcpHostAddress"] = new PlistString(TcpHostAddress),
            ["TcpPort"] = new PlistInteger(TcpPort),
            ["WaitForConnection"] = new PlistBoolean(IsWaitForConnection),
            ["RemoteConnectionAllowed"] = new PlistBoolean(IsRemoteConnectionAllowed),
        };
        if (Terminal is not null)
        {
            dict["Terminal"] = Terminal;
        }

        UtmSectionHelper.EmitUnknown(dict, UnknownKeys);
        return dict;
    }
}

/// <summary>Sound card entry (<c>Sound[]</c>).</summary>
public sealed record UtmSound
{
    /// <summary>Audio hardware, e.g. "intel-hda", "usb-audio" (raw string).</summary>
    public string Hardware { get; init; } = "";

    public IReadOnlyDictionary<string, PlistNode> UnknownKeys { get; init; } =
        new Dictionary<string, PlistNode>();

    private static readonly HashSet<string> Known = ["Hardware"];

    public static UtmSound FromPlist(PlistDictionary source) => new()
    {
        Hardware = source.GetString("Hardware", ""),
        UnknownKeys = UtmSectionHelper.CaptureUnknown(source, Known),
    };

    public PlistDictionary ToPlist()
    {
        var dict = new PlistDictionary
        {
            ["Hardware"] = new PlistString(Hardware),
        };
        UtmSectionHelper.EmitUnknown(dict, UnknownKeys);
        return dict;
    }
}
