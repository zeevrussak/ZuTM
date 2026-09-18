// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Core.Plist;
using ZuTM.Core.Utm;
using Xunit;

namespace ZuTM.Core.Tests.Utm;

/// <summary>A representative UTM v4 config.plist as macOS UTM would write it.</summary>
public static class UtmConfigFixtures
{
    public const string RealWorldConfigPlist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Backend</key>
            <string>QEMU</string>
            <key>ConfigurationVersion</key>
            <integer>4</integer>
            <key>Information</key>
            <dict>
                <key>Name</key>
                <string>Test VM</string>
                <key>Icon</key>
                <string></string>
                <key>IconCustom</key>
                <false/>
                <key>Notes</key>
                <string>made in UTM</string>
                <key>UUID</key>
                <string>6F0F0C6A-8DC0-47D1-A381-C0ABF67A8C8D</string>
            </dict>
            <key>System</key>
            <dict>
                <key>Architecture</key>
                <string>x86_64</string>
                <key>Target</key>
                <string>q35</string>
                <key>CPU</key>
                <string>host</string>
                <key>CPUFlagsAdd</key>
                <array><string>+sse4.2</string></array>
                <key>CPUFlagsRemove</key>
                <array><string>-pdpe1gb</string></array>
                <key>CPUCount</key>
                <integer>4</integer>
                <key>ForceMulticore</key>
                <false/>
                <key>MemorySize</key>
                <integer>4096</integer>
                <key>JITCacheSize</key>
                <integer>0</integer>
            </dict>
            <key>QEMU</key>
            <dict>
                <key>DebugLog</key>
                <false/>
                <key>UEFIBoot</key>
                <true/>
                <key>RNGDevice</key>
                <true/>
                <key>BalloonDevice</key>
                <true/>
                <key>TPMDevice</key>
                <false/>
                <key>Hypervisor</key>
                <true/>
                <key>TSO</key>
                <false/>
                <key>RTCLocalTime</key>
                <false/>
                <key>MachinePropertyOverride</key>
                <string></string>
                <key>AdditionalArguments</key>
                <array>
                    <dict>
                        <key>qemuArgument</key>
                        <array><string>-boot</string><string>menu=on</string></array>
                    </dict>
                </array>
            </dict>
            <key>Input</key>
            <dict>
                <key>UsbBusSupport</key>
                <string>Default</string>
                <key>UsbSharing</key>
                <false/>
                <key>MaximumUsbShare</key>
                <integer>3</integer>
            </dict>
            <key>Sharing</key>
            <dict>
                <key>DirectoryShareMode</key>
                <string>WebDAV</string>
                <key>DirectoryShareReadOnly</key>
                <false/>
                <key>ClipboardSharing</key>
                <true/>
            </dict>
            <key>Display</key>
            <array>
                <dict>
                    <key>Hardware</key>
                    <string>virtio-gpu-gl</string>
                    <key>VgaRamMib</key>
                    <integer>128</integer>
                    <key>DynamicResolution</key>
                    <true/>
                    <key>UpscalingFilter</key>
                    <string>Linear</string>
                    <key>DownscalingFilter</key>
                    <string>Linear</string>
                    <key>NativeResolution</key>
                    <false/>
                </dict>
            </array>
            <key>Drive</key>
            <array>
                <dict>
                    <key>ImageName</key>
                    <string>test-0.qcow2</string>
                    <key>ImageType</key>
                    <string>Disk</string>
                    <key>Interface</key>
                    <string>VirtIO</string>
                    <key>InterfaceVersion</key>
                    <integer>1</integer>
                    <key>Identifier</key>
                    <string>11111111-2222-3333-4444-555555555555</string>
                    <key>ReadOnly</key>
                    <false/>
                </dict>
                <dict>
                    <key>ImageType</key>
                    <string>CD</string>
                    <key>Interface</key>
                    <string>None</string>
                    <key>InterfaceVersion</key>
                    <integer>1</integer>
                    <key>Identifier</key>
                    <string>aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee</string>
                    <key>ReadOnly</key>
                    <true/>
                </dict>
            </array>
            <key>Network</key>
            <array>
                <dict>
                    <key>Mode</key>
                    <string>Shared</string>
                    <key>Hardware</key>
                    <string>virtio-net-pci</string>
                    <key>MacAddress</key>
                    <string>52:54:00:12:34:56</string>
                    <key>IsolateFromHost</key>
                    <false/>
                    <key>PortForward</key>
                    <array>
                        <dict>
                            <key>protocol</key>
                            <string>tcp</string>
                            <key>hostAddress</key>
                            <string></string>
                            <key>hostPort</key>
                            <integer>2222</integer>
                            <key>guestAddress</key>
                            <string></string>
                            <key>guestPort</key>
                            <integer>22</integer>
                        </dict>
                    </array>
                    <key>BridgeInterface</key>
                    <string></string>
                </dict>
            </array>
            <key>Serial</key>
            <array></array>
            <key>Sound</key>
            <array>
                <dict>
                    <key>Hardware</key>
                    <string>intel-hda</string>
                </dict>
            </array>
        </dict>
        </plist>
        """;
}

public class UtmConfigurationTests
{
    private static UtmConfiguration ParseFixture() =>
        UtmConfiguration.FromPlist(PlistDocument.ParseDictionary(
            System.Text.Encoding.UTF8.GetBytes(UtmConfigFixtures.RealWorldConfigPlist)));

    [Fact]
    public void Parses_RealWorldUtmBundleConfig()
    {
        var config = ParseFixture();

        Assert.Equal("QEMU", config.Backend);
        Assert.Equal(4, config.ConfigurationVersion);
        Assert.Equal("Test VM", config.Information.Name);
        Assert.Equal(new Guid("6f0f0c6a-8dc0-47d1-a381-c0abf67a8c8d"), config.Information.Uuid);
        Assert.Equal("x86_64", config.System.Architecture);
        Assert.Equal("q35", config.System.Target);
        Assert.Equal("host", config.System.Cpu);
        Assert.Equal(4, config.System.CpuCount);
        Assert.Equal(4096, config.System.MemorySizeMib);
        Assert.True(config.Qemu.HasUefiBoot);
        Assert.True(config.Qemu.HasHypervisor);
        Assert.Equal("virtio-gpu-gl", config.Displays[0].Hardware);
        Assert.True(config.Displays[0].IsDynamicResolution);
        Assert.Equal("WebDAV", config.Sharing.DirectoryShareMode);
        Assert.True(config.Sharing.HasClipboardSharing);
        Assert.Equal(2, config.Drives.Count);
        Assert.Equal("test-0.qcow2", config.Drives[0].ImageName);
        Assert.False(config.Drives[0].IsExternal);
        Assert.True(config.Drives[1].IsExternal); // no ImageName ⇒ external
        Assert.True(config.Drives[1].IsReadOnly); // external defaults read-only
        Assert.Equal("Shared", config.Networks[0].Mode);
        Assert.Equal("virtio-net-pci", config.Networks[0].Hardware);
        Assert.Equal(2222, config.Networks[0].PortForward[0].HostPort);
        Assert.Equal(22, config.Networks[0].PortForward[0].GuestPort);
        Assert.Equal("intel-hda", config.Sounds[0].Hardware);
        Assert.Equal(["-boot", "menu=on"], config.Qemu.AdditionalArguments[0].Tokens);
    }

    [Fact]
    public void RoundTrips_Losslessly_ThroughPlist()
    {
        var original = PlistDocument.ParseDictionary(
            System.Text.Encoding.UTF8.GetBytes(UtmConfigFixtures.RealWorldConfigPlist));

        var model = UtmConfiguration.FromPlist(original);
        var rewritten = model.ToPlist();

        PlistAssert.Equal(original, rewritten);
    }

    [Fact]
    public void RoundTrips_UnknownKeys_AndUnknownEnumValues()
    {
        var original = PlistDocument.ParseDictionary(
            System.Text.Encoding.UTF8.GetBytes(UtmConfigFixtures.RealWorldConfigPlist));

        // Simulate a future UTM adding a root key, a QEMU key, and an enum value ZuTM has never seen.
        original["FutureThing"] = PlistFactory.Array(new PlistInteger(7));
        ((PlistDictionary)original["QEMU"]!)["SpiceServerPort"] = new PlistInteger(5931);
        ((PlistDictionary)((PlistArray)original["Network"]!)[0])["Mode"] = new PlistString("Quantum");

        var model = UtmConfiguration.FromPlist(original);
        var rewritten = model.ToPlist();

        PlistAssert.Equal(original, rewritten);
        Assert.Equal("Quantum", model.Networks[0].Mode); // preserved raw, not clobbered
    }

    [Fact]
    public void SerialTerminal_DictionaryRoundTripsVerbatim()
    {
        var original = PlistDocument.ParseDictionary(
            System.Text.Encoding.UTF8.GetBytes(UtmConfigFixtures.RealWorldConfigPlist));

        var terminal = PlistFactory.Dict(
            ("Theme", new PlistString("Solarized")),
            ("FontSize", new PlistInteger(14)));
        var serialEntry = PlistFactory.Dict(
            ("Mode", new PlistString("Terminal")),
            ("Target", new PlistString("Auto")),
            ("Hardware", new PlistString("isa-serial")),
            ("Terminal", terminal));
        ((PlistArray)original["Serial"]!).Add(serialEntry);

        var model = UtmConfiguration.FromPlist(original);

        Assert.NotNull(model.Serials[0].Terminal);
        Assert.Equal("Solarized", model.Serials[0].Terminal!.GetString("Theme"));
        Assert.Equal(14, model.Serials[0].Terminal!.GetInteger("FontSize", 0));
    }

    [Fact]
    public void Rejects_AppleBackend()
    {
        var plist = PlistFactory.Dict(
            ("Backend", new PlistString("Apple")),
            ("ConfigurationVersion", new PlistInteger(4)));

        var error = Assert.Throws<UtmConfigurationException>(() => UtmConfiguration.FromPlist(plist));
        Assert.Contains("Apple backend", error.Message);
    }

    [Fact]
    public void Rejects_LegacyBackend()
    {
        var plist = PlistFactory.Dict(("Backend", new PlistString("Unknown")));

        var error = Assert.Throws<UtmConfigurationException>(() => UtmConfiguration.FromPlist(plist));
        Assert.Contains("legacy", error.Message);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(0)]
    public void Rejects_TooOldVersions(int version)
    {
        var plist = PlistFactory.Dict(
            ("Backend", new PlistString("QEMU")),
            ("ConfigurationVersion", new PlistInteger(version)));

        Assert.Throws<UtmConfigurationException>(() => UtmConfiguration.FromPlist(plist));
    }

    [Fact]
    public void Rejects_TooNewVersions()
    {
        var plist = PlistFactory.Dict(
            ("Backend", new PlistString("QEMU")),
            ("ConfigurationVersion", new PlistInteger(UtmConfiguration.CurrentVersion + 1)));

        var error = Assert.Throws<UtmConfigurationException>(() => UtmConfiguration.FromPlist(plist));
        Assert.Contains("upgrade ZuTM", error.Message);
    }

    [Fact]
    public void Defaults_MatchUtmDefaults()
    {
        var plist = PlistFactory.Dict(
            ("Backend", new PlistString("QEMU")),
            ("ConfigurationVersion", new PlistInteger(4)),
            ("Information", new PlistDictionary()),
            ("System", new PlistDictionary()),
            ("QEMU", new PlistDictionary()),
            ("Input", new PlistDictionary()),
            ("Sharing", new PlistDictionary()));

        var config = UtmConfiguration.FromPlist(plist);

        Assert.Equal("x86_64", config.System.Architecture);
        Assert.Equal(512, config.System.MemorySizeMib);
        Assert.True(config.Qemu.HasRngDevice);
        Assert.True(config.Qemu.HasBalloonDevice);
        Assert.True(config.Qemu.HasHypervisor);
        Assert.Equal("Default", config.Input.UsbBusSupport);
        Assert.Equal(3, config.Input.MaximumUsbShare);
        Assert.Equal("None", config.Sharing.DirectoryShareMode);
        Assert.NotEqual(Guid.Empty, config.Information.Uuid);
    }

    [Fact]
    public void MissingSections_ThrowClearErrors()
    {
        var plist = PlistFactory.Dict(
            ("Backend", new PlistString("QEMU")),
            ("ConfigurationVersion", new PlistInteger(4)));

        var error = Assert.Throws<UtmConfigurationException>(() => UtmConfiguration.FromPlist(plist));
        Assert.Contains("Information", error.Message);
    }

    [Fact]
    public void WrittenConfig_Is_XmlPlist()
    {
        var bytes = PlistDocument.Write(ParseFixture().ToPlist(), PlistFormat.Xml);
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.StartsWith("<?xml", text);
        Assert.Contains("<!DOCTYPE plist", text);
    }
}
