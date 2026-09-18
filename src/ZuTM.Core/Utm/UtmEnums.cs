// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
// Clean-room model of the UTM configuration format (ConfigurationVersion 4).
// All enum-like fields are stored as raw strings so values ZuTM does not know
// round-trip losslessly to UTM. See docs/utm-compatibility.md.

using ZuTM.Core.Plist;

namespace ZuTM.Core.Utm;

/// <summary>Well-known values for string-backed UTM fields, with tolerant parsing helpers.</summary>
public static class UtmValues
{
    public static class Backend
    {
        public const string Qemu = "QEMU";
        public const string Apple = "Apple";
        public const string Unknown = "Unknown";
    }

    public static class DriveImageType
    {
        public const string None = "None";
        public const string Disk = "Disk";
        public const string Cd = "CD";
        public const string Bios = "BIOS";
        public const string LinuxKernel = "LinuxKernel";
        public const string LinuxInitrd = "LinuxInitrd";
        public const string LinuxDtb = "LinuxDTB";
    }

    public static class DriveInterface
    {
        public const string None = "None";
        public const string Ide = "IDE";
        public const string Scsi = "SCSI";
        public const string Sd = "SD";
        public const string Mtd = "MTD";
        public const string Floppy = "Floppy";
        public const string Pflash = "PFlash";
        public const string Virtio = "VirtIO";
        public const string Nvme = "NVMe";
        public const string Usb = "USB";
    }

    public static class NetworkMode
    {
        public const string Emulated = "Emulated";
        public const string Shared = "Shared";
        public const string Host = "Host";
        public const string Bridged = "Bridged";
    }

    public static class SerialMode
    {
        public const string Terminal = "Terminal";
        public const string TcpClient = "TcpClient";
        public const string TcpServer = "TcpServer";
        public const string Ptty = "Ptty";
    }

    public static class SerialTarget
    {
        public const string Auto = "Auto";
        public const string Manual = "Manual";
        public const string Gdb = "GDB";
        public const string Monitor = "Monitor";
    }

    public static class DirectoryShareMode
    {
        public const string None = "None";
        public const string WebDav = "WebDAV";
        public const string VirtFs = "VirtFS";
    }

    public static class UsbBusSupport
    {
        public const string None = "None";
        public const string Default = "Default";
        public const string Usb2 = "USB 2.0";
        public const string Usb3 = "USB 3.0";
    }

    public static class ScalingFilter
    {
        public const string Linear = "Linear";
        public const string Nearest = "Nearest";
    }

    public static class PortForwardProtocol
    {
        public const string Tcp = "TCP";
        public const string Udp = "UDP";
    }
}

/// <summary>Shared plumbing for section models: capture and re-emit unknown plist keys.</summary>
internal static class UtmSectionHelper
{
    /// <summary>Returns all entries of <paramref name="source"/> whose key is not in <paramref name="knownKeys"/>.</summary>
    public static IReadOnlyDictionary<string, PlistNode> CaptureUnknown(PlistDictionary source, IReadOnlySet<string> knownKeys)
    {
        Dictionary<string, PlistNode> unknown = [];
        foreach (var (key, value) in source)
        {
            if (!knownKeys.Contains(key))
            {
                unknown[key] = value;
            }
        }

        return unknown;
    }

    /// <summary>Appends captured unknown entries onto the serialized output dictionary.</summary>
    public static void EmitUnknown(PlistDictionary target, IReadOnlyDictionary<string, PlistNode> unknown)
    {
        foreach (var (key, value) in unknown)
        {
            target[key] = value;
        }
    }
}
