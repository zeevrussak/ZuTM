// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
// Runtime device control over QMP: CD-ROM medium swapping (mount/eject ISOs)
// and USB host-device passthrough (device_add/device_del usb-host).
// All node/device ids use the zutm- prefix so ZuTM never touches devices it
// did not create and the guest sees a normal hot-plug sequence.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZuTM.Core.Utm;

namespace ZuTM.Core.Qemu;

/// <summary>One removable-media (CD) tray of a running VM as reported by QMP query-block.</summary>
public sealed record CdTrayInfo(
    string DeviceId,
    bool HasMedia,
    string? MediumFile,
    string? MediumNodeName,
    bool IsHotPlugged)
{
    /// <summary>Frontend node name for a medium ZuTM added at runtime (safe to blockdev-del).</summary>
    public bool MediumIsZuTMNode => MediumNodeName is not null
        && MediumNodeName.StartsWith(QmpDeviceExtensions.ZuTMIsoNodePrefix, StringComparison.Ordinal);
}

public sealed record AttachedUsbDevice(
    string DeviceId,
    ushort VendorId,
    ushort ProductId,
    string? ProductName);

/// <summary>CD tray discovery, ISO mount/eject and USB host passthrough over one QMP connection.</summary>
public static partial class QmpDeviceExtensions
{
    internal const string ZuTMIsoNodePrefix = "zutm-iso-";
    internal const string ZuTMHotCdPrefix = "zutm-usb-cd-";
    internal const string ZuTMUsbIdPrefix = "zutm-usb-";
    private const string ZuTMHotXhciId = "zutm-xhci-hot";

    // -- CD trays -----------------------------------------------------------------

    /// <summary>Lists removable-media trays (config CD drives plus any ZuTM-hotplugged USB CD).</summary>
    public static async Task<IReadOnlyList<CdTrayInfo>> QueryCdTraysAsync(
        this QmpClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var block = await client.ExecuteAsync("query-block", null, cancellationToken);
        return ParseBlockList(block);
    }

    /// <summary>
    /// query-block reply shape (tray entries only; qdev is the frontend device id):
    ///   {"device":"zutm-cd-0","qdev":"zutm-cd-dev-0","removable":true,
    ///    "inserted":{"file":"…","node-name":"…", …}}
    /// "inserted" is absent when the tray is empty. device_add'd devices report
    /// their qdev as a QOM path ("/machine/peripheral/&lt;id&gt;/…") — the plain id
    /// is recovered from the path; tray QMP commands accept both forms.
    /// </summary>
    internal static IReadOnlyList<CdTrayInfo> ParseBlockList(JsonElement queryBlockResult)
    {
        if (queryBlockResult.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<CdTrayInfo> trays = [];
        foreach (var entry in queryBlockResult.EnumerateArray())
        {
            var backendId = entry.TryGetProperty("device", out var device) ? device.GetString() : null;
            var qdev = entry.TryGetProperty("qdev", out var qdevElement) ? qdevElement.GetString() : null;
            if (entry.TryGetProperty("removable", out var removable) && removable.ValueKind == JsonValueKind.True)
            {
                var reported = qdev ?? backendId;
                if (string.IsNullOrEmpty(reported))
                {
                    continue; // no addressable device — cannot operate this tray
                }

                // device_add'd peripherals: recover the plain id we chose from
                // the QOM path so device_del can address it by name.
                string deviceId = reported;
                var isHotPlugged = reported.StartsWith(ZuTMHotCdPrefix, StringComparison.Ordinal);
                if (reported.StartsWith("/machine/peripheral/", StringComparison.Ordinal))
                {
                    var segments = reported.Split('/');
                    if (segments.Length < 3)
                    {
                        continue;
                    }

                    deviceId = segments[3];
                    isHotPlugged = true;
                }

                string? mediumFile = null;
                string? mediumNode = null;
                if (entry.TryGetProperty("inserted", out var inserted) && inserted.ValueKind == JsonValueKind.Object)
                {
                    mediumFile = inserted.TryGetProperty("file", out var file) ? file.GetString() : null;
                    mediumNode = inserted.TryGetProperty("node-name", out var node) ? node.GetString() : null;
                }

                var hasMedia = inserted.ValueKind == JsonValueKind.Object;
                trays.Add(new CdTrayInfo(deviceId, hasMedia, mediumFile, mediumNode, isHotPlugged));
            }
        }

        return trays;
    }

    /// <summary>
    /// Mounts an ISO into this VM: an empty tray is preferred, then media is
    /// replaced on the first tray; a VM without any CD device gets a USB CD
    /// hot-plugged around the ISO. Returns the tray now holding the ISO.
    /// </summary>
    public static Task<CdTrayInfo> MountIsoAsync(
        this QmpClient client, string isoPath, CancellationToken cancellationToken = default) =>
        client.MountIsoAsync(tray: null, isoPath, cancellationToken);

    public static async Task<CdTrayInfo> MountIsoAsync(
        this QmpClient client, CdTrayInfo? tray, string isoPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        tray ??= (await client.QueryCdTraysAsync(cancellationToken) is { Count: > 0 } trays
            ? trays.FirstOrDefault(t => !t.HasMedia) ?? trays[0]
            : null);

        var node = ZuTMIsoNodePrefix + Guid.NewGuid().ToString("N")[..12];
        await client.BlockdevAddFileAsync(node, isoPath, cancellationToken);
        try
        {
            if (tray is null)
            {
                // No tray exists in this VM (config has no CD drive): build a
                // USB CD frontend around the freshly added block node.
                var hotCdId = ZuTMHotCdPrefix + Guid.NewGuid().ToString("N")[..12];
                try
                {
                    await client.AddUsbStorageCdAsync(hotCdId, node, cancellationToken);
                }
                catch (QmpException ex) when (LooksLikeMissingUsbBus(ex))
                {
                    // Configs without a display add no USB controller; give the
                    // CD a bus and retry once.
                    await client.AddHotXhciAsync(cancellationToken);
                    await client.AddUsbStorageCdAsync(hotCdId, node, cancellationToken);
                }

                return new CdTrayInfo(hotCdId, HasMedia: true, isoPath, node, IsHotPlugged: true);
            }

            // Physical CD semantics: the tray must be OPEN before the medium
            // can leave or enter ("Tray of the device is not open" otherwise).
            await client.OpenTrayAsync(tray.DeviceId, cancellationToken);
            try
            {
                if (tray.HasMedia)
                {
                    await client.ExecuteAsync("blockdev-remove-medium",
                        new Dictionary<string, object?> { ["id"] = tray.DeviceId }, cancellationToken);
                }

                await client.ExecuteAsync("blockdev-insert-medium", new Dictionary<string, object?>
                {
                    ["id"] = tray.DeviceId,
                    ["node-name"] = node,
                }, cancellationToken);
            }
            finally
            {
                await client.CloseTrayAsync(tray.DeviceId, cancellationToken);
            }

            if (tray.MediumIsZuTMNode && tray.MediumNodeName is { } previous)
            {
                // Only ZuTM-added nodes may be deleted; -drive backends from the
                // launch plan must stay (they back the empty tray for re-use).
                try
                {
                    await client.ExecuteAsync("blockdev-del",
                        new Dictionary<string, object?> { ["node-name"] = previous }, cancellationToken);
                }
                catch (QmpException)
                {
                    // Detached but not yet deletable (device still releasing it):
                    // the node is harmless and disappears with the VM.
                }
            }

            return tray with { HasMedia = true, MediumFile = isoPath, MediumNodeName = node };
        }
        catch
        {
            await TryDeleteNodeAsync(client, node, cancellationToken);
            throw;
        }
    }

    /// <summary>Ejects the tray's medium; a ZuTM-hotplugged USB CD is removed entirely.</summary>
    public static async Task EjectAsync(
        this QmpClient client, CdTrayInfo tray, TimeSpan? hotUnplugTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tray);

        if (tray.IsHotPlugged)
        {
            await client.ExecuteAsync("device_del",
                new Dictionary<string, object?> { ["id"] = tray.DeviceId }, cancellationToken);
            await client.WaitForDeviceDeletedAsync(tray.DeviceId, hotUnplugTimeout ?? TimeSpan.FromSeconds(3), cancellationToken);
            if (tray.MediumIsZuTMNode && tray.MediumNodeName is { } node)
            {
                await TryDeleteNodeAsync(client, node, cancellationToken);
            }

            return;
        }

        if (!tray.HasMedia)
        {
            return;
        }

        await client.OpenTrayAsync(tray.DeviceId, cancellationToken);
        try
        {
            await client.ExecuteAsync("blockdev-remove-medium",
                new Dictionary<string, object?> { ["id"] = tray.DeviceId }, cancellationToken);
        }
        finally
        {
            await client.CloseTrayAsync(tray.DeviceId, cancellationToken);
        }

        if (tray.MediumIsZuTMNode && tray.MediumNodeName is { } medium)
        {
            await TryDeleteNodeAsync(client, medium, cancellationToken);
        }
    }

    /// <summary>
    /// Opens the tray (physical eject position); required before medium
    /// insert/remove. Forced — a guest-locked tray must not wedge the host UI.
    /// </summary>
    public static Task OpenTrayAsync(
        this QmpClient client, string deviceId, CancellationToken cancellationToken = default) =>
        client.ExecuteAsync("blockdev-open-tray",
            new Dictionary<string, object?> { ["id"] = deviceId, ["force"] = true }, cancellationToken);

    /// <summary>Closes the tray (spins the medium up). Best-effort: failure never masks the mount result.</summary>
    public static async Task CloseTrayAsync(
        this QmpClient client, string deviceId, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.ExecuteAsync("blockdev-close-tray",
                new Dictionary<string, object?> { ["id"] = deviceId }, cancellationToken);
        }
        catch (QmpException)
        {
            // Some removable frontends (usb-storage) have no closable tray —
            // the medium stays functional; the guest re-scans on its own.
        }
    }

    /// <summary>Attaches a file as a raw read-only block node the CD frontend can consume.</summary>
    public static Task BlockdevAddFileAsync(
        this QmpClient client, string nodeName, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.ExecuteAsync("blockdev-add", new Dictionary<string, object?>
        {
            ["node-name"] = nodeName,
            ["driver"] = "raw",
            ["read-only"] = true,
            ["file"] = new Dictionary<string, object?>
            {
                ["driver"] = "file",
                ["filename"] = path,
            },
        }, cancellationToken);
    }

    private static async Task TryDeleteNodeAsync(QmpClient client, string node, CancellationToken cancellationToken)
    {
        try
        {
            await client.ExecuteAsync("blockdev-del",
                new Dictionary<string, object?> { ["node-name"] = node }, cancellationToken);
        }
        catch (QmpException)
        {
            // Node still held by the tray/device — it dies with the VM; not fatal.
        }
    }

    /// <summary>Waits for the DEVICE_DELETED event of a specific device id (best-effort).</summary>
    public static async Task WaitForDeviceDeletedAsync(
        this QmpClient client, string deviceId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            while (await client.Events.WaitToReadAsync(linked.Token))
            {
                while (client.Events.TryRead(out var message))
                {
                    if (message.Event != "DEVICE_DELETED")
                    {
                        continue;
                    }

                    var raw = message.Raw;
                    if ((raw.TryGetProperty("device", out var device) && device.GetString() == deviceId)
                        || (raw.TryGetProperty("path", out var path) && path.GetString()?.EndsWith("/" + deviceId, StringComparison.Ordinal) == true))
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Guest ack did not arrive in time — detach proceeds regardless.
        }
    }

    // -- USB host passthrough -----------------------------------------------------

    /// <summary>
    /// Hot-attaches a host USB device (matched by VID/PID — QEMU claims the
    /// first present, unclaimed match). When the VM has no USB bus (headless
    /// configs add no controller) an xHCI controller is added first and the
    /// attach retried. "Duplicate ID" errors read as already-attached.
    /// </summary>
    public static async Task<AttachedUsbDevice> AttachUsbHostDeviceAsync(
        this QmpClient client, ushort vendorId, ushort productId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var deviceId = UsbDeviceId(vendorId, productId);

        async Task Attach()
        {
            await client.ExecuteAsync("device_add", new Dictionary<string, object?>
            {
                ["driver"] = "usb-host",
                ["id"] = deviceId,
                ["vendorid"] = vendorId,
                ["productid"] = productId,
            }, cancellationToken);
        }

        try
        {
            await Attach();
        }
        catch (QmpException ex) when (ex.Message.Contains("Duplicate ID", StringComparison.OrdinalIgnoreCase))
        {
            // Already attached — nothing to do.
        }
        catch (QmpException ex) when (LooksLikeMissingUsbBus(ex))
        {
            await client.AddHotXhciAsync(cancellationToken);
            await Attach();
        }

        return new AttachedUsbDevice(deviceId, vendorId, productId, null);
    }

    /// <summary>Hot-detaches a usb-host device by its ZuTM device id.</summary>
    public static async Task DetachUsbHostDeviceAsync(
        this QmpClient client, string deviceId, TimeSpan? unplugTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        await client.ExecuteAsync("device_del",
            new Dictionary<string, object?> { ["id"] = deviceId }, cancellationToken);
        await client.WaitForDeviceDeletedAsync(deviceId, unplugTimeout ?? TimeSpan.FromSeconds(3), cancellationToken);
    }

    /// <summary>Lists ZuTM-routed usb-host devices currently attached to the guest (HMP "info usb").</summary>
    public static async Task<IReadOnlyList<AttachedUsbDevice>> QueryAttachedUsbHostDevicesAsync(
        this QmpClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var output = await client.HumanCommandAsync("info usb", cancellationToken);
        return ParseInfoUsb(output);
    }

    /// <summary>
    /// "info usb" rows look like:
    ///   Device 0.2, Port 2, Speed 480 Mb/s, Product My Drive, ID: zutm-usb-0781-5567
    /// Only rows carrying a ZuTM usb-host id are returned.
    /// </summary>
    public static IReadOnlyList<AttachedUsbDevice> ParseInfoUsb(string infoUsbOutput)
    {
        List<AttachedUsbDevice> devices = [];
        foreach (var line in infoUsbOutput.Split('\n'))
        {
            var match = InfoUsbIdRow().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var id = match.Groups["id"].Value;
            if (!TryParseUsbDeviceId(id, out var vendorId, out var productId))
            {
                continue;
            }

            devices.Add(new AttachedUsbDevice(id, vendorId, productId, match.Groups["name"].Value));
        }

        return devices;
    }

    /// <summary>Deterministic device id for one VID/PID pair (one routed device per pair).</summary>
    public static string UsbDeviceId(ushort vendorId, ushort productId) =>
        $"{ZuTMUsbIdPrefix}{vendorId:x4}-{productId:x4}";

    /// <summary>
    /// Attaches every USB device routed in the VM's ZuTM state. A device that
    /// is absent or claimed by the host produces a warning, never a launch
    /// failure — a plugged-in-tomorrow USB stick must not stop the VM booting.
    /// </summary>
    public static async Task<IReadOnlyList<string>> AttachRoutedUsbDevicesAsync(
        this QmpClient client, IReadOnlyList<ZutmUsbDevice> devices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        List<string> warnings = [];
        foreach (var device in devices)
        {
            try
            {
                await client.AttachUsbHostDeviceAsync(device.VendorId, device.ProductId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is QmpException or InvalidOperationException)
            {
                warnings.Add($"USB device {device.DisplayName} could not be routed into the VM: {ex.Message}");
            }
        }

        return warnings;
    }

    public static bool TryParseUsbDeviceId(string deviceId, out ushort vendorId, out ushort productId)
    {
        vendorId = 0;
        productId = 0;
        if (!deviceId.StartsWith(ZuTMUsbIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = deviceId[ZuTMUsbIdPrefix.Length..].Split('-');
        return parts.Length == 2
            && ushort.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vendorId)
            && ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out productId);
    }

    private static Task<JsonElement> AddUsbStorageCdAsync(
        this QmpClient client, string deviceId, string nodeName, CancellationToken cancellationToken) =>
        client.ExecuteAsync("device_add", new Dictionary<string, object?>
        {
            ["driver"] = "usb-storage",
            ["drive"] = nodeName,
            ["removable"] = true,
            ["id"] = deviceId,
        }, cancellationToken);

    private static Task<JsonElement> AddHotXhciAsync(this QmpClient client, CancellationToken cancellationToken) =>
        client.ExecuteAsync("device_add", new Dictionary<string, object?>
        {
            ["driver"] = "qemu-xhci",
            ["id"] = ZuTMHotXhciId,
        }, cancellationToken);

    private static bool LooksLikeMissingUsbBus(QmpException ex)
    {
        // Error JSON arrives verbatim (fake servers may escape quotes as \u0027).
        var message = ex.Message.Replace("\\u0027", "'", StringComparison.OrdinalIgnoreCase);
        return message.Contains("No 'usb-bus' bus found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no free USB", StringComparison.OrdinalIgnoreCase)
            || message.Contains("could not find USB", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no USB bus", StringComparison.OrdinalIgnoreCase)
            || message.Contains("could not be initialized", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"Product\s+(?<name>.+?),\s*ID:\s*(?<id>\S+)")]
    private static partial Regex InfoUsbIdRow();
}
