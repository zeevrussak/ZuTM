// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
// Lossless config editor: every field maps onto `with` transforms of the
// UTM model — unknown keys ride along untouched (FR-08/60).

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZuTM.Core.Utm;

namespace ZuTM.App;

public sealed partial class VMConfigEditorDialog : ContentDialog
{
    private readonly UtmBundle _bundle;

    public VMConfigEditorDialog(UtmBundle bundle)
    {
        _bundle = bundle;
        InitializeComponent();
        PrimaryButtonClick += OnSave;
        LoadFields();
    }

    private void LoadFields()
    {
        var c = _bundle.Configuration;
        NameBox.Text = c.Information.Name;
        NotesBox.Text = c.Information.Notes;

        ArchitectureBox.Text = c.System.Architecture;
        TargetBox.Text = c.System.Target;
        CpuBox.Text = c.System.Cpu;
        CpuCountBox.Value = c.System.CpuCount;
        MemoryBox.Value = c.System.MemorySizeMib;

        UefiToggle.IsOn = c.Qemu.HasUefiBoot;
        HypervisorToggle.IsOn = c.Qemu.HasHypervisor;
        RtcToggle.IsOn = c.Qemu.HasRtcLocalTime;
        DebugLogToggle.IsOn = c.Qemu.HasDebugLog;

        DisplayBox.Text = c.Displays.FirstOrDefault()?.Hardware ?? string.Empty;

        NetworkModeBox.Text = c.Networks.FirstOrDefault()?.Mode ?? UtmValues.NetworkMode.Shared;
        NetworkHardwareBox.Text = c.Networks.FirstOrDefault()?.Hardware ?? string.Empty;
        MacBox.Text = c.Networks.FirstOrDefault()?.MacAddress ?? string.Empty;

        var sharingMode = c.Sharing.DirectoryShareMode;
        for (var i = 0; i < SharingModeBox.Items.Count; i++)
        {
            if ((SharingModeBox.Items[i] as ComboBoxItem)?.Tag as string == sharingMode)
            {
                SharingModeBox.SelectedIndex = i;
                break;
            }
        }

        SharingReadOnlyToggle.IsOn = c.Sharing.IsDirectoryShareReadOnly;
        ClipboardToggle.IsOn = c.Sharing.HasClipboardSharing;
    }

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            args.Cancel = true;
            ErrorBar.Message = "Name cannot be empty.";
            ErrorBar.IsOpen = true;
            return;
        }

        var memory = (int)Math.Clamp(MemoryBox.Value, 128, 131_072);
        var cpuCount = (int)Math.Clamp(CpuCountBox.Value, 0, 64);
        var c = _bundle.Configuration;

        var configuration = c with
        {
            Information = c.Information with { Name = name, Notes = NotesBox.Text },
            System = c.System with
            {
                Architecture = ArchitectureBox.Text.Trim().ToLowerInvariant(),
                Target = TargetBox.Text.Trim(),
                Cpu = string.IsNullOrWhiteSpace(CpuBox.Text) ? "default" : CpuBox.Text.Trim(),
                CpuCount = cpuCount,
                MemorySizeMib = memory,
            },
            Qemu = c.Qemu with
            {
                HasUefiBoot = UefiToggle.IsOn,
                HasHypervisor = HypervisorToggle.IsOn,
                HasRtcLocalTime = RtcToggle.IsOn,
                HasDebugLog = DebugLogToggle.IsOn,
            },
            Sharing = c.Sharing with
            {
                DirectoryShareMode = (SharingModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? UtmValues.DirectoryShareMode.None,
                IsDirectoryShareReadOnly = SharingReadOnlyToggle.IsOn,
                HasClipboardSharing = ClipboardToggle.IsOn,
            },
        };

        // Display: empty means headless (clear the list); otherwise keep every
        // display entry and edit the first one's hardware.
        var displayHardware = DisplayBox.Text.Trim();
        configuration = configuration with
        {
            Displays = displayHardware.Length == 0
                ? []
                : [.. configuration.Displays.Select((d, i) => i == 0 ? d with { Hardware = displayHardware } : d)],
        };

        // Network: edit the first adapter if one exists.
        if (configuration.Networks.Count > 0)
        {
            var first = configuration.Networks[0];
            configuration = configuration with
            {
                Networks = [.. configuration.Networks.Select((n, i) =>
                    i == 0 ? n with
                    {
                        Mode = NetworkModeBox.Text.Trim(),
                        Hardware = NetworkHardwareBox.Text.Trim(),
                        MacAddress = MacBox.Text.Trim(),
                    } : n)],
            };
            _ = first;
        }

        try
        {
            _bundle.Configuration = configuration;
            _bundle.Save();
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
    }
}
