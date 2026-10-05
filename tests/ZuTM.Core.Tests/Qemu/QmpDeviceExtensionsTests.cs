// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Text.Json;
using ZuTM.Core.Qemu;
using ZuTM.Core.Utm;
using Xunit;

namespace ZuTM.Core.Tests.Qemu;

public class QmpCdMediaTests
{
    private const string BlockListJson = """
        [
          {"device":"zutm-drive-0","qdev":"virtio-disk0","removable":false,
           "inserted":{"file":"C:\\vms\\disk.qcow2","node-name":"zutm-drive-0","ro":false}},
          {"device":"zutm-cd-0","qdev":"zutm-cd-dev-0","removable":true,
           "inserted":{"file":"C:\\isos\\install.iso","node-name":"zutm-cd-0","ro":true}},
          {"device":"zutm-cd-1","qdev":"zutm-cd-dev-1","removable":true},
          {"device":"floppy0","removable":true},
          {"device":"ide2-cd0","qdev":"/machine/unattached/device[15]","removable":true,"tray_open":false},
          {"device":"","qdev":"/machine/peripheral/probe-usbcd/probe-usbcd.0/legacy[0]","removable":true,
           "inserted":{"file":"C:\\isos\\usb.iso","node-name":"probe-iso","ro":true}},
          {"device":"zutm-usb-cd-abc123","qdev":"zutm-usb-cd-abc123","removable":true,
           "inserted":{"file":"C:\\isos\\other.iso","node-name":"zutm-iso-abc123","ro":true}}
        ]
        """;

    [Fact]
    public void ParseBlockList_ReadsTraysMediaAndHotplugState()
    {
        var result = JsonDocument.Parse(BlockListJson).RootElement;

        var trays = QmpDeviceExtensions.ParseBlockList(result);

        Assert.Equal(6, trays.Count); // removable only; the qcow2 disk is not a tray
        var tray0 = Assert.Single(trays, t => t.DeviceId == "zutm-cd-dev-0");
        Assert.True(tray0.HasMedia);
        Assert.Equal(@"C:\isos\install.iso", tray0.MediumFile);
        Assert.Equal("zutm-cd-0", tray0.MediumNodeName);
        Assert.False(tray0.MediumIsZuTMNode);

        var tray1 = Assert.Single(trays, t => t.DeviceId == "zutm-cd-dev-1");
        Assert.False(tray1.HasMedia);
        Assert.Null(tray1.MediumFile);

        // The board's anonymous drive reports a QOM path — still operable:
        // QMP tray commands accept paths as device ids.
        var board = Assert.Single(trays, t => t.DeviceId == "/machine/unattached/device[15]");
        Assert.False(board.HasMedia);
        Assert.False(board.IsHotPlugged);

        // device_add'd peripherals carry the plain id inside their QOM path.
        var peripheral = Assert.Single(trays, t => t.DeviceId == "probe-usbcd");
        Assert.True(peripheral.IsHotPlugged);
        Assert.True(peripheral.HasMedia);
        Assert.Equal(@"C:\isos\usb.iso", peripheral.MediumFile);

        var hot = Assert.Single(trays, t => t.DeviceId == "zutm-usb-cd-abc123");
        Assert.True(hot.IsHotPlugged);
        Assert.True(hot.MediumIsZuTMNode);
    }

    [Fact]
    public async Task MountIso_IntoEmptyTray_AddsNodeAndInsertsMedium()
    {
        await using var server = await FakeQmpServer.StartAsync();
        server.OnCommand = (command, _) => command == "query-block"
            ? new { return_ = JsonSerializer.Deserialize<JsonElement>(BlockListJson) }
            : new { return_ = new { } };
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);
        var tray = (await client.QueryCdTraysAsync()).Single(t => t.DeviceId == "zutm-cd-dev-1");

        var result = await client.MountIsoAsync(tray, @"C:\isos\new.iso");

        Assert.True(result.HasMedia);
        Assert.Equal(@"C:\isos\new.iso", result.MediumFile);
        Assert.StartsWith("zutm-iso-", result.MediumNodeName);

        var blockdevAdd = server.Commands.Single(c => c.Command == "blockdev-add");
        Assert.Contains(@"""filename"":""C:\\isos\\new.iso""", blockdevAdd.Arguments);
        Assert.Contains(@"""read-only"":true", blockdevAdd.Arguments);

        var insert = server.Commands.Single(c => c.Command == "blockdev-insert-medium");
        Assert.Contains(@"""id"":""zutm-cd-dev-1""", insert.Arguments);
        Assert.Contains(result.MediumNodeName!, insert.Arguments);
        // Empty tray: no remove/del round-trip.
        Assert.DoesNotContain(server.Commands, c => c.Command == "blockdev-remove-medium");
    }

    [Fact]
    public async Task MountIso_OverLegacyMedium_RemovesButNeverDeletesLaunchBackends()
    {
        await using var server = await FakeQmpServer.StartAsync();
        server.OnCommand = (command, _) => command == "query-block"
            ? new { return_ = JsonSerializer.Deserialize<JsonElement>(BlockListJson) }
            : new { return_ = new { } };
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);
        var tray = (await client.QueryCdTraysAsync()).Single(t => t.DeviceId == "zutm-cd-dev-0");

        var result = await client.MountIsoAsync(tray, @"C:\isos\new.iso");

        var remove = server.Commands.Single(c => c.Command == "blockdev-remove-medium");
        Assert.Contains(@"""id"":""zutm-cd-dev-0""", remove.Arguments);
        // The launch-time backend (zutm-cd-0) must survive: it backs the tray
        // for the next mount; blockdev-del is only for ZuTM-added nodes.
        Assert.DoesNotContain(server.Commands, c => c.Command == "blockdev-del");
        Assert.Equal(@"C:\isos\new.iso", result.MediumFile);
    }

    [Fact]
    public async Task MountIso_OverZuTMNode_DeletesReplacedNode()
    {
        await using var server = await FakeQmpServer.StartAsync();
        server.OnCommand = (command, _) => command == "query-block"
            ? new { return_ = JsonSerializer.Deserialize<JsonElement>(BlockListJson) }
            : new { return_ = new { } };
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);
        var hot = (await client.QueryCdTraysAsync()).Single(t => t.DeviceId == "zutm-usb-cd-abc123");

        await client.MountIsoAsync(hot, @"C:\isos\replacement.iso");

        var del = Assert.Single(server.Commands, c => c.Command == "blockdev-del");
        Assert.Contains("zutm-iso-abc123", del.Arguments);
    }

    [Fact]
    public async Task MountIso_WithoutAnyTray_HotPlugsUsbCd()
    {
        await using var server = await FakeQmpServer.StartAsync();
        server.OnCommand = (command, _) => command == "query-block"
            ? new { return_ = JsonSerializer.Deserialize<JsonElement>("[]") }
            : new { return_ = new { } };
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);

        var tray = await client.MountIsoAsync(@"C:\isos\installer.iso");

        var add = server.Commands.Single(c => c.Command == "device_add");
        Assert.Contains(@"""driver"":""usb-storage""", add.Arguments);
        Assert.Contains(@"""removable"":true", add.Arguments);
        Assert.Contains(tray.DeviceId, add.Arguments);
        Assert.True(tray.IsHotPlugged);
        Assert.True(tray.HasMedia);
    }

    [Fact]
    public async Task MountIso_WithoutTrayOrUsbBus_AddsXhciBetween()
    {
        var usbStorageAttempts = 0;
        await using var server = await FakeQmpServer.StartAsync();
        server.OnCommand = (command, _) => command switch
        {
            "query-block" => new { return_ = JsonSerializer.Deserialize<JsonElement>("[]") },
            "device_add" when ++usbStorageAttempts == 1 =>
                new { error_ = new { class_ = "GenericError", desc = "No 'usb-bus' bus found for device 'usb-storage'" } },
            _ => new { return_ = new { } },
        };
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);

        var tray = await client.MountIsoAsync(@"C:\isos\installer.iso");

        Assert.True(tray.IsHotPlugged);
        var adds = server.Commands.Where(c => c.Command == "device_add").ToList();
        Assert.Equal(3, adds.Count); // failed usb-storage, xhci controller, retried usb-storage
        Assert.Contains(@"""driver"":""qemu-xhci""", adds[1].Arguments);
        Assert.Contains(tray.DeviceId, adds[2].Arguments);
    }

    [Fact]
    public async Task Eject_RemovesMedium_AndDeletesOnlyZuTMNodes()
    {
        await using var server = await FakeQmpServer.StartAsync();
        server.OnCommand = (_, _) => new { return_ = new { } };
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);

        // Legacy launch tray: medium removed, backend stays.
        await client.EjectAsync(new CdTrayInfo("zutm-cd-dev-0", true, @"C:\isos\a.iso", "zutm-cd-0", IsHotPlugged: false));
        Assert.Contains(server.Commands, c => c.Command == "blockdev-remove-medium");
        Assert.DoesNotContain(server.Commands, c => c.Command == "blockdev-del");

        // ZuTM runtime node: removed AND deleted.
        await client.EjectAsync(new CdTrayInfo("zutm-cd-dev-1", true, @"C:\isos\b.iso", "zutm-iso-fff", IsHotPlugged: false));
        Assert.Equal(2, server.Commands.Count(c => c.Command == "blockdev-remove-medium"));
        Assert.Equal(1, server.Commands.Count(c => c.Command == "blockdev-del"));
    }

    [Fact]
    public async Task Eject_HotPluggedCd_DeletesDeviceAndNode()
    {
        await using var server = await FakeQmpServer.StartAsync();
        server.OnCommand = (_, _) => new { return_ = new { } };
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);
        var tray = new CdTrayInfo("zutm-usb-cd-abc", true, @"C:\isos\a.iso", "zutm-iso-abc", IsHotPlugged: true);

        await client.EjectAsync(tray, hotUnplugTimeout: TimeSpan.Zero);

        var del = server.Commands.Single(c => c.Command == "device_del");
        Assert.Contains(@"""id"":""zutm-usb-cd-abc""", del.Arguments);
        var nodeDel = server.Commands.Single(c => c.Command == "blockdev-del");
        Assert.Contains("zutm-iso-abc", nodeDel.Arguments);
    }

    [Fact]
    public async Task MountIso_CleansUpNode_WhenInsertFails()
    {
        await using var server = await FakeQmpServer.StartAsync();
        server.OnCommand = (command, _) => command switch
        {
            "query-block" => new { return_ = JsonSerializer.Deserialize<JsonElement>(BlockListJson) },
            "blockdev-insert-medium" => new { error_ = new { class_ = "GenericError", desc = "busy" } },
            _ => new { return_ = new { } },
        };
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);
        var tray = (await client.QueryCdTraysAsync()).Single(t => t.DeviceId == "zutm-cd-dev-1");

        await Assert.ThrowsAsync<QmpException>(() => client.MountIsoAsync(tray, @"C:\isos\x.iso"));

        // The orphan node is deleted so a retry cannot leak block backends.
        var del = server.Commands.Single(c => c.Command == "blockdev-del");
        Assert.Contains("zutm-iso-", del.Arguments);
    }
}

public class QmpUsbHostTests
{
    private const string InfoUsb = """
        (qemu) info usb
          Device 0.1, Port 1, Speed 12 Mb/s, Product QEMU USB Tablet, ID: tablet
          Device 0.2, Port 2, Speed 480 Mb/s, Product QEMU USB Keyboard, ID: kbd
          Device 0.3, Port 3, Speed 480 Mb/s, Product SanDisk Cruzer, ID: zutm-usb-0781-5567
          Device 0.4, Port 4, Speed 12 Mb/s, Product Microsoft Mouse, ID: zutm-usb-045e-0745
        """;

    [Fact]
    public void ParseInfoUsb_ReturnsOnlyZuTMRoutedDevices()
    {
        var devices = QmpDeviceExtensions.ParseInfoUsb(InfoUsb);

        Assert.Equal(2, devices.Count);
        Assert.Equal("zutm-usb-0781-5567", devices[0].DeviceId);
        Assert.Equal((ushort)0x0781, devices[0].VendorId);
        Assert.Equal((ushort)0x5567, devices[0].ProductId);
        Assert.Equal("SanDisk Cruzer", devices[0].ProductName);
        Assert.Equal((ushort)0x045E, devices[1].VendorId);
    }

    [Fact]
    public void UsbDeviceId_RoundTrips()
    {
        Assert.Equal("zutm-usb-0781-5567", QmpDeviceExtensions.UsbDeviceId(0x0781, 0x5567));
        Assert.True(QmpDeviceExtensions.TryParseUsbDeviceId("zutm-usb-045e-0745", out var vid, out var pid));
        Assert.Equal((ushort)0x045E, vid);
        Assert.Equal((ushort)0x0745, pid);
        Assert.False(QmpDeviceExtensions.TryParseUsbDeviceId("tablet", out _, out _));
    }

    [Fact]
    public async Task Attach_SendsUsbHostWithVidPid()
    {
        await using var server = await FakeQmpServer.StartAsync();
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);

        var attached = await client.AttachUsbHostDeviceAsync(0x0781, 0x5567);

        Assert.Equal("zutm-usb-0781-5567", attached.DeviceId);
        var add = server.Commands.Single(c => c.Command == "device_add");
        Assert.Contains(@"""driver"":""usb-host""", add.Arguments);
        Assert.Contains(@"""id"":""zutm-usb-0781-5567""", add.Arguments);
        Assert.Contains(@"""vendorid"":1921", add.Arguments); // 0x0781 decimal in JSON
        Assert.Contains(@"""productid"":21863", add.Arguments); // 0x5567 decimal in JSON
    }

    [Fact]
    public async Task Attach_DuplicateId_IsAlreadyAttached()
    {
        await using var server = await FakeQmpServer.StartAsync();
        server.OnCommand = (_, _) => new
        {
            error_ = new { class_ = "GenericError", desc = "Duplicate ID 'zutm-usb-0781-5567' for device" },
        };
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);

        var attached = await client.AttachUsbHostDeviceAsync(0x0781, 0x5567);

        Assert.Equal("zutm-usb-0781-5567", attached.DeviceId);
        Assert.Equal(1, server.Commands.Count(c => c.Command == "device_add"));
    }

    [Fact]
    public async Task Attach_WithoutUsbBus_AddsXhciAndRetries()
    {
        var deviceAddAttempts = 0;
        await using var server = await FakeQmpServer.StartAsync();
        server.OnCommand = (command, _) => command == "device_add" && ++deviceAddAttempts == 1
            ? new { error_ = new { class_ = "GenericError", desc = "Device 'usb-host' could not be initialized" } }
            : new { return_ = new { } };
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);

        var attached = await client.AttachUsbHostDeviceAsync(0x045E, 0x0745);

        Assert.Equal("zutm-usb-045e-0745", attached.DeviceId);
        var adds = server.Commands.Where(c => c.Command == "device_add").ToList();
        Assert.Equal(3, adds.Count); // failed attach, xhci controller, retried attach
        Assert.Contains(@"""driver"":""qemu-xhci""", adds[1].Arguments);
    }

    [Fact]
    public async Task Attach_OtherErrors_Surface()
    {
        await using var server = await FakeQmpServer.StartAsync();
        server.OnCommand = (_, _) => new
        {
            error_ = new { class_ = "GenericError", desc = "libusb: could not claim the device [Library access error]" },
        };
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);

        await Assert.ThrowsAsync<QmpException>(
            () => client.AttachUsbHostDeviceAsync(0x0781, 0x5567));
    }

    [Fact]
    public async Task Detach_DeletesDeviceById()
    {
        await using var server = await FakeQmpServer.StartAsync();
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);

        await client.DetachUsbHostDeviceAsync("zutm-usb-0781-5567", unplugTimeout: TimeSpan.Zero);

        var del = server.Commands.Single(c => c.Command == "device_del");
        Assert.Contains(@"""id"":""zutm-usb-0781-5567""", del.Arguments);
    }

    [Fact]
    public async Task AttachRoutedDevices_AbsentDevice_YieldsWarningOthersStillAttach()
    {
        var failNext = true;
        await using var server = await FakeQmpServer.StartAsync();
        server.OnCommand = (command, _) =>
        {
            if (command == "device_add" && failNext)
            {
                failNext = false;
                return new { error_ = new { class_ = "GenericError", desc = "usb-host: no matching device" } };
            }

            return new { return_ = new { } };
        };
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);

        var warnings = await client.AttachRoutedUsbDevicesAsync(
        [
            new ZutmUsbDevice { VendorId = 0x1111, ProductId = 0x2222, Name = "Gone Device" },
            new ZutmUsbDevice { VendorId = 0x0781, ProductId = 0x5567, Name = "Present Device" },
        ]);

        var warning = Assert.Single(warnings);
        Assert.Contains("Gone Device", warning);
        Assert.Equal(2, server.Commands.Count(c => c.Command == "device_add"));
    }
}
