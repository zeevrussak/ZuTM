// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZuTM.App.Services;
using ZuTM.Core.Utm;

namespace ZuTM.App;

public sealed partial class NewVmDialog : ContentDialog
{
    private readonly VmLibraryService _library;

    public VmItemViewModel? CreatedVm { get; private set; }

    public NewVmDialog(VmLibraryService library)
    {
        _library = library;
        InitializeComponent();
        ArchitectureBox.SelectionChanged += (_, _) => SyncTargets();
        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private void SyncTargets()
    {
        var isX86 = GetTag(ArchitectureBox) == "x86_64";
        TargetBox.SelectedIndex = isX86 ? 0 : 1;
        foreach (ComboBoxItem item in TargetBox.Items)
        {
            var tag = item.Tag as string;
            item.IsEnabled = isX86 ? tag == "q35" : tag == "virt";
        }
    }

    private static string? GetTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            args.Cancel = true;
            CreateInfoBar.Message = "Enter a name.";
            CreateInfoBar.IsOpen = true;
            return;
        }

        var architecture = GetTag(ArchitectureBox) ?? "x86_64";
        var target = GetTag(TargetBox) ?? "q35";
        var diskGib = (int)Math.Clamp(DiskBox.Value, 0, 2048);

        var deferral = args.GetDeferral();
        try
        {
            var isX86 = architecture == "x86_64";
            var configuration = new UtmConfiguration
            {
                Information = new UtmInformation { Name = name },
                System = new UtmSystem
                {
                    Architecture = architecture,
                    Target = target,
                    MemorySizeMib = (int)Math.Clamp(MemoryBox.Value, 128, 131_072),
                    CpuCount = (int)Math.Clamp(CpuBox.Value, 0, 64),
                },
                Qemu = new UtmQemu { HasUefiBoot = UefiBox.IsChecked == true && isX86 },
                Displays =
                [
                    new UtmDisplay { Hardware = isX86 ? "virtio-gpu-pci" : "virtio-gpu-pci" },
                ],
                Networks =
                [
                    new UtmNetwork
                    {
                        Mode = UtmValues.NetworkMode.Shared,
                        Hardware = isX86 ? "virtio-net-pci" : "virtio-net-device",
                        MacAddress = GenerateMac(),
                    },
                ],
                Sounds = [new UtmSound { Hardware = isX86 ? "intel-hda" : "usb-audio" }],
            };

            var vm = await _library.CreateAsync(configuration);

            if (diskGib > 0)
            {
                var diskName = $"{name.ToLowerInvariant().Replace(' ', '-')}-0.qcow2";
                var diskPath = Path.Combine(vm.Bundle.DataDirectory, Sanitize(diskName));
                await Task.Run(() => _library.CreateDiskImage(diskPath, diskGib * 1024L));

                vm.Bundle.Configuration = vm.Bundle.Configuration with
                {
                    Drives =
                    [
                        new UtmDrive
                        {
                            ImageName = Path.GetFileName(diskPath),
                            ImageType = UtmValues.DriveImageType.Disk,
                            Interface = UtmValues.DriveInterface.Virtio,
                        },
                    ],
                };
                await Task.Run(() => vm.Bundle.Save());
            }

            CreatedVm = vm;
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            CreateInfoBar.Message = ex.Message;
            CreateInfoBar.IsOpen = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static string Sanitize(string fileName) =>
        string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));

    /// <summary>Locally administered, unicast MAC in the QEMU-friendly 52:54:00 OUI.</summary>
    private static string GenerateMac()
    {
        var bytes = new byte[3];
        Random.Shared.NextBytes(bytes);
        return $"52:54:00:{bytes[0]:X2}:{bytes[1]:X2}:{bytes[2]:X2}";
    }
}
