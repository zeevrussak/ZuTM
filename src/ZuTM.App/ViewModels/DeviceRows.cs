// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
// Rows for the live device sections (VM viewer) and the USB routing pickers
// (settings + creation dialogs). Plain get-only properties — x:Bind targets.

using ZuTM.Core.Qemu;

namespace ZuTM.App.ViewModels;

/// <summary>One removable-media tray of a running VM.</summary>
public sealed class CdTrayRow
{
    public CdTrayRow(CdTrayInfo tray)
    {
        Tray = tray;
        Title = tray.HasMedia ? "CD/DVD — media inserted" : "CD/DVD — empty";
        Detail = tray.MediumFile ?? "no media";
        HasMedia = tray.HasMedia;
    }

    public CdTrayInfo Tray { get; }

    public string Title { get; }

    public string Detail { get; }

    public bool HasMedia { get; }
}

/// <summary>
/// One host USB device in a routing list. IsAttached means "currently routed
/// into the running VM" in the viewer and "selected for routing on start" in
/// the settings and creation dialogs. The device object is internal — the
/// XAML type-info generator would otherwise try to activate it (it has
/// required members); templates bind only to the scalar properties below.
/// </summary>
public sealed class UsbHostRow
{
    public UsbHostRow(UsbHostDevice device, bool isAttached)
    {
        Device = device;
        IsAttached = isAttached;
    }

    internal UsbHostDevice Device { get; }

    public bool IsAttached { get; }

    public string Title => Device.Name;

    public string Detail => $"VID {Device.VendorId:X4} · PID {Device.ProductId:X4}";
}
