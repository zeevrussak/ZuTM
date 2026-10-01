// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZuTM.App.Services;
using ZuTM.Core.Qemu;
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
        FormatBox.SelectionChanged += (_, _) =>
            AllocationBox.IsEnabled = SelectedFormat().SupportsFixedAllocation();
        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private DiskImageFormat SelectedFormat() => (GetTag(FormatBox) ?? "qcow2") switch
    {
        "raw" => DiskImageFormat.Raw,
        "vhd" => DiskImageFormat.Vhd,
        "vdi" => DiskImageFormat.Vdi,
        "vmdk" => DiskImageFormat.Vmdk,
        _ => DiskImageFormat.Qcow2,
    };

    private DiskAllocationMode SelectedAllocation() =>
        GetTag(AllocationBox) == "fixed" && SelectedFormat().SupportsFixedAllocation()
            ? DiskAllocationMode.Fixed
            : DiskAllocationMode.Expanding;

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

    private async void OnBrowseIsoClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        if (App.MainWindow is { } window)
        {
            // Unpackaged WinUI 3 pickers must be bound to a window handle.
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        }

        picker.FileTypeFilter.Add(".iso");
        picker.FileTypeFilter.Add(".img");

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            IsoBox.Text = file.Path;
            ClearIsoButton.IsEnabled = true;
        }
    }

    private void OnClearIsoClick(object sender, RoutedEventArgs e)
    {
        IsoBox.Text = string.Empty;
        ClearIsoButton.IsEnabled = false;
    }

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
        var isoPath = IsoBox.Text.Trim();
        if (isoPath.Length > 0 && !File.Exists(isoPath))
        {
            args.Cancel = true;
            CreateInfoBar.Message = "The selected installer ISO does not exist.";
            CreateInfoBar.IsOpen = true;
            return;
        }

        var deferral = args.GetDeferral();
        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = false;
        CreateProgress.IsActive = true;
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
                Qemu = new UtmQemu
                {
                    HasUefiBoot = UefiBox.IsChecked == true && isX86,
                    HasBalloonDevice = BalloonBox.IsChecked == true,
                },
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

            // Drive list order is the boot order: the installer ISO goes first,
            // so a blank-disk VM boots from the ISO until it is ejected.
            var drives = new List<UtmDrive>();
            if (isoPath.Length > 0)
            {
                var cd = await Task.Run(() => vm.Bundle.ImportDriveImage(isoPath));
                drives.Add(cd with
                {
                    ImageType = UtmValues.DriveImageType.Cd,
                    Interface = isX86 ? UtmValues.DriveInterface.Ide : UtmValues.DriveInterface.Virtio,
                    IsReadOnly = true,
                });
            }

            if (diskGib > 0)
            {
                var format = SelectedFormat();
                var diskName = $"{name.ToLowerInvariant().Replace(' ', '-')}-0{format.FileExtension()}";
                var diskPath = Path.Combine(vm.Bundle.DataDirectory, Sanitize(diskName));
                var spec = new DiskImageSpec
                {
                    Path = diskPath,
                    SizeBytes = diskGib * 1024L * 1024L * 1024L,
                    Format = format,
                    Allocation = SelectedAllocation(),
                };
                await Task.Run(() => _library.CreateDiskImage(spec));

                drives.Add(new UtmDrive
                {
                    ImageName = Path.GetFileName(diskPath),
                    ImageType = UtmValues.DriveImageType.Disk,
                    Interface = UtmValues.DriveInterface.Virtio,
                });
            }

            if (drives.Count > 0)
            {
                vm.Bundle.Configuration = vm.Bundle.Configuration with { Drives = [.. drives] };
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
            CreateProgress.IsActive = false;
            IsPrimaryButtonEnabled = true;
            IsSecondaryButtonEnabled = true;
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
