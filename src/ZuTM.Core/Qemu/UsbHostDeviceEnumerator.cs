// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
// Host-side USB device discovery for passthrough routing (VM viewer, VM
// settings, New VM dialog). Enumeration is read-only SetupAPI/CfgMgr32 —
// no driver is installed or changed; QEMU claims the device via libusb.

using System.Runtime.InteropServices;

namespace ZuTM.Core.Qemu;

/// <summary>A USB device physically present on this Windows host.</summary>
public sealed record UsbHostDevice
{
    public required ushort VendorId { get; init; }

    public required ushort ProductId { get; init; }

    /// <summary>Friendly name or device description (falls back to the instance id).</summary>
    public string Name { get; init; } = "";

    public string InstanceId { get; init; } = "";

    /// <summary>Deterministic QEMU device id used for this VID/PID pair.</summary>
    public string DeviceId => QmpDeviceExtensions.UsbDeviceId(VendorId, ProductId);

    public override string ToString() => $"{Name} (VID {VendorId:X4}, PID {ProductId:X4})";
}

/// <summary>
/// Enumerates present USB devices (device interface class GUID_DEVINTERFACE_USB_DEVICE)
/// with their VID/PID. USB hubs and root hubs are excluded — routing a hub makes no
/// sense. Works on x64 and ARM64; enumeration errors yield an empty list.
/// </summary>
public static class UsbHostDeviceEnumerator
{
    /// <summary>GUID_DEVINTERFACE_USB_DEVICE.</summary>
    private static readonly Guid UsbDeviceInterfaceClass = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    private const int DigcfPresent = 0x0000_0002;
    private const int DigcfDeviceInterface = 0x0000_0010;
    private const int SpdrpDeviceDesc = 0x0000_0000;
    private const int SpdrpCompatibleIds = 0x0000_0002;
    private const int SpdrpFriendlyName = 0x0000_000C;
    private const uint CmGetDeviceIdNone = 0;

    public static IReadOnlyList<UsbHostDevice> List()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        List<UsbHostDevice> devices = [];
        var handle = SetupDiGetClassDevsW(in UsbDeviceInterfaceClass, IntPtr.Zero, IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (handle == new IntPtr(-1))
        {
            return devices;
        }

        try
        {
            var seenInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(handle, IntPtr.Zero, in UsbDeviceInterfaceClass, index, ref interfaceData))
                {
                    break; // ERROR_NO_MORE_ITEMS
                }

                if (!TryGetDeviceInstance(handle, ref interfaceData, out var devInfo, out var instanceId)
                    || !seenInstances.Add(instanceId)
                    || !TryParseVidPid(instanceId, out var vendorId, out var productId)
                    || IsHub(handle, devInfo))
                {
                    continue;
                }

                devices.Add(new UsbHostDevice
                {
                    VendorId = vendorId,
                    ProductId = productId,
                    Name = GetRegistryString(handle, devInfo, SpdrpFriendlyName)
                        ?? GetRegistryString(handle, devInfo, SpdrpDeviceDesc)
                        ?? instanceId,
                    InstanceId = instanceId,
                });
            }
        }
        catch (DllNotFoundException)
        {
            return [];
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(handle);
        }

        return devices;
    }

    /// <summary>
    /// Runs the interface-detail two-call dance purely to obtain the
    /// SP_DEVINFO_DATA devnode (and, via it, registry properties + instance id).
    /// </summary>
    private static bool TryGetDeviceInstance(
        IntPtr handle, ref SpDeviceInterfaceData interfaceData, out SpDevinfoData devInfo, out string instanceId)
    {
        devInfo = default;
        instanceId = "";
        SetupDiGetDeviceInterfaceDetailW(handle, ref interfaceData, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);
        if (requiredSize <= 0)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(requiredSize);
        try
        {
            // cbSize is the fixed header (DWORD) + the first WCHAR of DevicePath,
            // aligned to the pointer size: 8 on x64/ARM64, 6 on x86.
            Marshal.WriteInt32(buffer, IntPtr.Size == 4 ? 6 : 8);
            devInfo = new SpDevinfoData { CbSize = Marshal.SizeOf<SpDevinfoData>() };
            if (!SetupDiGetDeviceInterfaceDetailW(handle, ref interfaceData, buffer, requiredSize, out _, ref devInfo))
            {
                return false;
            }

            var length = 512;
            var idBuffer = Marshal.AllocHGlobal(length * sizeof(char));
            try
            {
                return SetupDiGetDeviceInstanceIdW(handle, ref devInfo, idBuffer, length, out _)
                    && (instanceId = Marshal.PtrToStringUni(idBuffer) ?? "")!.Length > 0;
            }
            finally
            {
                Marshal.FreeHGlobal(idBuffer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Hubs carry the USB hub class (Class_09) compatible id; devices don't.</summary>
    private static bool IsHub(IntPtr handle, in SpDevinfoData devInfo)
    {
        var compatibleIds = GetRegistryString(handle, devInfo, SpdrpCompatibleIds);
        return compatibleIds is not null && compatibleIds.Contains("Class_09", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetRegistryString(IntPtr handle, in SpDevinfoData devInfo, int property)
    {
        var length = 1024;
        var buffer = Marshal.AllocHGlobal(length * sizeof(char));
        try
        {
            if (!SetupDiGetDeviceRegistryPropertyW(handle, devInfo, property, out _, buffer, length * sizeof(char), out _))
            {
                return null;
            }

            // REG_SZ / REG_MULTI_SZ: stop at the first terminator.
            var text = Marshal.PtrToStringUni(buffer) ?? "";
            return text.Length == 0 ? null : text;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryParseVidPid(string instanceId, out ushort vendorId, out ushort productId)
    {
        vendorId = 0;
        productId = 0;
        // USB\VID_046D&PID_C52B\... — VID/PID are four hex digits each.
        var vidIndex = instanceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        var pidIndex = instanceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (vidIndex < 0 || pidIndex < 0 || vidIndex + 8 > instanceId.Length || pidIndex + 8 > instanceId.Length)
        {
            return false;
        }

        return ushort.TryParse(instanceId.AsSpan(vidIndex + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out vendorId)
            && ushort.TryParse(instanceId.AsSpan(pidIndex + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out productId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public int CbSize;
        public Guid ClassGuid;
        public int DevInst;
        public nint Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern IntPtr SetupDiGetClassDevsW(
        in Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiEnumDeviceInterfaces")]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, in Guid interfaceClassGuid, int memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize, out int requiredSize, ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceInfoSet, in SpDevinfoData deviceInfoData, int property, out int propertyRegDataType,
        IntPtr propertyBuffer, int propertyBufferSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, IntPtr deviceInstanceId, int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiDestroyDeviceInfoList")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
