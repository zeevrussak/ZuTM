// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZuTM.App.Services;
using ZuTM.App.ViewModels;
using ZuTM.Core.Net;
using ZuTM.Core.Qemu;
using ZuTM.Core.Utm;

namespace ZuTM.App;

public sealed partial class NewVmDialog : ContentDialog
{
    private readonly VmLibraryService _library;
    private readonly HashSet<UsbHostDevice> _routedUsbDevices = [];
    private CancellationTokenSource? _resolveCancellation;
    private string? _lastResolvedUrl;

    public VmItemViewModel? CreatedVm { get; private set; }

    public NewVmDialog(VmLibraryService library)
    {
        _library = library;
        InitializeComponent();
        ArchitectureBox.SelectionChanged += (_, _) =>
        {
            SyncTargets();
            RefreshStreamCatalog();
        };
        FormatBox.SelectionChanged += (_, _) =>
            AllocationBox.IsEnabled = SelectedFormat().SupportsFixedAllocation();
        PrimaryButtonClick += OnPrimaryButtonClick;
        RefreshStreamCatalog();
        _ = LoadUsbRouteListAsync();
    }

    private async Task LoadUsbRouteListAsync()
    {
        IReadOnlyList<UsbHostDevice> devices;
        try
        {
            devices = await _library.ListUsbHostDevicesAsync();
        }
        catch (Exception)
        {
            devices = [];
        }

        UsbRouteList.ItemsSource = devices
            .OrderBy(d => d.Name, StringComparer.CurrentCulture)
            .Select(d => new UsbHostRow(d, _routedUsbDevices.Contains(d)))
            .ToList();
        UsbStatusText.Text = devices.Count == 0 ? "No USB devices found on this host." : "";
    }

    private void OnUsbRescanClick(object sender, RoutedEventArgs e) => _ = LoadUsbRouteListAsync();

    private void OnUsbRouteChecked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is UsbHostRow row)
        {
            _routedUsbDevices.Add(row.Device);
        }
    }

    private void OnUsbRouteUnchecked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is UsbHostRow row)
        {
            _routedUsbDevices.Remove(row.Device);
        }
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

    // -- Streaming installer ISOs ---------------------------------------------------
    // QEMU's curl block driver reads the ISO straight off the distributor's
    // server; ZuTM only has to pin the exact URL of the current release.

    private void RefreshStreamCatalog()
    {
        var architecture = GetTag(ArchitectureBox) ?? "x86_64";
        _resolveCancellation?.Cancel();
        StreamCatalogBox.Items.Clear();
        StreamCatalogBox.Items.Add(new ComboBoxItem
        {
            Content = "Choose a Linux distribution…",
            Tag = "",
            IsSelected = true,
        });
        foreach (var offering in DistroIsoCatalog.ForArchitecture(architecture))
        {
            var item = new ComboBoxItem
            {
                Content = offering.DisplayName,
                Tag = offering.Id,
            };
            ToolTipService.SetToolTip(item, offering.Description);
            StreamCatalogBox.Items.Add(item);
        }

        StreamStatusText.Text = string.Empty;
    }

    private async void OnStreamCatalogSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var id = GetTag(StreamCatalogBox);
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        var offering = DistroIsoCatalog.All.FirstOrDefault(o => o.Id == id);
        if (offering is null)
        {
            return;
        }

        _resolveCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _resolveCancellation = cancellation;

        ResolveProgress.IsActive = true;
        StreamStatusText.Text = $"Looking up the current {offering.DisplayName} ISO…";
        try
        {
            var url = await new DistroIsoResolver().ResolveUrlAsync(offering, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            _lastResolvedUrl = url;
            IsoUrlBox.Text = url;
            StreamStatusText.Text =
                $"Resolved {url[(url.LastIndexOf('/') + 1)..]} from {new Uri(url).Host} — it downloads into the VM when you click Create.";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection or dialog close.
        }
        catch (Exception ex)
        {
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            StreamCatalogBox.SelectedIndex = 0; // back to the placeholder (no-op handler)
            StreamStatusText.Text = $"Could not reach {offering.DisplayName}: {ex.Message} Paste an ISO URL below instead.";
        }
        finally
        {
            if (_resolveCancellation == cancellation)
            {
                _resolveCancellation = null;
                ResolveProgress.IsActive = false;
            }
        }
    }

    private void OnIsoUrlTextChanged(object sender, TextChangedEventArgs e)
    {
        if (string.Equals(IsoUrlBox.Text.Trim(), _lastResolvedUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // A hand-typed URL deselects the catalog entry.
        _lastResolvedUrl = null;
        if (StreamCatalogBox.SelectedIndex > 0)
        {
            StreamCatalogBox.SelectedIndex = 0;
        }

        StreamStatusText.Text = string.Empty;
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

        var isoUrl = IsoUrlBox.Text.Trim();
        if (isoPath.Length > 0 && isoUrl.Length > 0)
        {
            args.Cancel = true;
            CreateInfoBar.Message = "Choose either a local ISO file or an ISO URL — not both.";
            CreateInfoBar.IsOpen = true;
            return;
        }

        if (isoUrl.Length > 0 && RemoteImage.GetValidationError(isoUrl) is { } urlError)
        {
            args.Cancel = true;
            CreateInfoBar.Message = urlError;
            CreateInfoBar.IsOpen = true;
            return;
        }

        _resolveCancellation?.Cancel();
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
                Input = new UtmInput
                {
                    UsbBusSupport = GetTag(UsbBusBox) ?? UtmValues.UsbBusSupport.Default,
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
            if (isoUrl.Length > 0)
            {
                // Official-distribution ISO: resolve happened above; the ISO
                // downloads into the bundle now (QEMU on Windows cannot stream
                // network images at run time). QEMU's curl driver limitation
                // is why this is a download, not a streamed CD backend.
                var lastReportedPercent = -1.0;
                var progress = new Progress<RemoteIsoDownloadProgress>(p =>
                {
                    // Progress<T> can coalesce; report at most every whole percent.
                    var percent = Math.Floor(p.Percent ?? 0);
                    if (percent <= lastReportedPercent && p.Percent is not null)
                    {
                        return;
                    }

                    lastReportedPercent = percent;
                    StreamStatusText.Text = p.Percent is { } value
                        ? $"Downloading {RemoteImage.SuggestedFileName(isoUrl)}… {value:F1}%"
                        : $"Downloading {RemoteImage.SuggestedFileName(isoUrl)}… {p.BytesReceived / (1024.0 * 1024.0):F1} MiB";
                });
                drives.Add(await vm.Bundle.ImportRemoteIsoAsync(
                    isoUrl, isX86 ? UtmValues.DriveInterface.Ide : UtmValues.DriveInterface.Virtio, progress));
                StreamStatusText.Text = $"Downloaded {RemoteImage.SuggestedFileName(isoUrl)} into the VM.";
            }
            else if (isoPath.Length > 0)
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

            if (_routedUsbDevices.Count > 0)
            {
                vm.Bundle.State = vm.Bundle.State with
                {
                    UsbDevices = [.. _routedUsbDevices.Select(d => new ZutmUsbDevice
                    {
                        VendorId = d.VendorId,
                        ProductId = d.ProductId,
                        Name = d.Name,
                    })],
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
