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

    /// <summary>Slow work (ISO fetch/import, disk create) for VmActivityService to run after this dialog closes.</summary>
    public VmCreationPlan? CreationPlan { get; private set; }

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
        Opened += (_, _) => NameBox.Focus(FocusState.Keyboard);
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
            UpdateDestinationNotice();
        }
    }

    private void OnClearIsoClick(object sender, RoutedEventArgs e)
    {
        IsoBox.Text = string.Empty;
        ClearIsoButton.IsEnabled = false;
        UpdateDestinationNotice();
    }

    /// <summary>Tells the user where a chosen installer ISO will land before committing to Create.</summary>
    private void UpdateDestinationNotice()
    {
        var hasSource = IsoBox.Text.Trim().Length > 0 || IsoUrlBox.Text.Trim().Length > 0;
        if (!hasSource)
        {
            IsoDestinationText.Visibility = Visibility.Collapsed;
            return;
        }

        var folder = _library.Settings.DownloadsFolder;
        IsoDestinationText.Text = string.IsNullOrWhiteSpace(folder)
            ? "The ISO downloads into this VM's bundle when you click Create."
            : $"The ISO downloads to {folder} and is attached from there — change it in Settings ▸ Downloads folder.";
        IsoDestinationText.Visibility = Visibility.Visible;
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
            UpdateDestinationNotice();
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
        UpdateDestinationNotice();
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

        if (isoUrl.Length == 0 && StreamCatalogBox.SelectedIndex > 0)
        {
            // A catalog distro is picked but its URL has not landed in the
            // box yet — creating now would silently produce a VM without the
            // installer it was promised.
            args.Cancel = true;
            CreateInfoBar.Message = $"Still resolving the {((StreamCatalogBox.SelectedItem as ComboBoxItem)?.Content ?? "selected")} ISO — wait a moment, or paste an ISO URL below.";
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
                    new UtmDisplay { Hardware = "virtio-gpu-pci" },
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

            // Everything slow happens after the dialog closes: the service
            // fetches/copies the ISO and creates the disk with live progress,
            // pause/resume and cancel on the VM's row and detail pane.
            var format = SelectedFormat();
            var diskName = diskGib > 0
                ? Sanitize($"{name.ToLowerInvariant().Replace(' ', '-')}-0{format.FileExtension()}")
                : null;
            CreationPlan = new VmCreationPlan(
                IsoUrl: isoUrl.Length > 0 ? isoUrl : null,
                LocalIsoPath: isoPath.Length > 0 ? isoPath : null,
                DriveInterface: isX86 ? UtmValues.DriveInterface.Ide : UtmValues.DriveInterface.Virtio,
                Disk: diskName is null
                    ? null
                    : new DiskImageSpec
                    {
                        Path = Path.Combine(vm.Bundle.DataDirectory, diskName),
                        SizeBytes = diskGib * 1024L * 1024L * 1024L,
                        Format = format,
                        Allocation = SelectedAllocation(),
                    });

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
