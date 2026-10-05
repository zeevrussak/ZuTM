// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZuTM.App.Services;
using ZuTM.App.ViewModels;
using ZuTM.Core.Qemu;
using ZuTM.Core.Utm;

namespace ZuTM.App;

public sealed partial class VmDetailView : UserControl
{
    public static readonly DependencyProperty VmProperty = DependencyProperty.Register(
        nameof(Vm), typeof(VmItemViewModel), typeof(VmDetailView), new PropertyMetadata(null, OnVmChanged));

    private readonly DispatcherQueue? _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    /// <summary>Library facade for live QMP operations; MainWindow injects it.</summary>
    public VmLibraryService? Library { get; set; }

    public VmItemViewModel? Vm
    {
        get => (VmItemViewModel?)GetValue(VmProperty);
        set => SetValue(VmProperty, value);
    }

    public bool HasVm => Vm is not null;

    public string MemoryText => $"{Vm?.MemoryMib ?? 0} MiB";

    public string CpuText
    {
        get
        {
            var count = Vm?.Bundle.Configuration.System.CpuCount ?? 0;
            return count > 0 ? count.ToString() : "Match host";
        }
    }

    public string BootText => Vm?.Bundle.Configuration.Qemu.HasUefiBoot == true ? "UEFI" : "BIOS";

    public string DisplayText => Vm?.Bundle.Configuration.Displays.Count > 0
        ? string.Join(", ", Vm.Bundle.Configuration.Displays.Select(d => d.Hardware))
        : "Headless (serial)";

    public ObservableCollection<DetailRow> NetworkRows { get; } = [];

    public ObservableCollection<DetailRow> DriveRows { get; } = [];

    /// <summary>True when a stopped VM still has an installer ISO attached.</summary>
    public bool HasEjectableIso => Vm is { CanStart: true } vm
        && vm.Bundle.Configuration.Drives.Any(d => d.ImageType == UtmValues.DriveImageType.Cd);

    /// <summary>Live media/USB sections are available while the VM runs (or is paused).</summary>
    public bool CanHotPlug => Vm?.Status is VmStatus.Running or VmStatus.Paused;

    public ObservableCollection<CdTrayRow> MediaRows { get; } = [];

    public ObservableCollection<UsbHostRow> UsbRows { get; } = [];

    public VmDetailView()
    {
        InitializeComponent();
        DataContext = this;
    }

    private static void OnVmChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (VmDetailView)sender;
        if (e.OldValue is VmItemViewModel previous)
        {
            previous.PropertyChanged -= view.OnVmPropertyChanged;
        }

        if (e.NewValue is VmItemViewModel current)
        {
            current.PropertyChanged += view.OnVmPropertyChanged;
        }

        view.Rebuild();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VmItemViewModel.SerialPort) && Vm is not null)
        {
            Terminal.Endpoint = Vm.SerialPort;
        }

        // Start/stop flips the eject affordance (only meaningful while stopped)
        // and the live media/USB sections (only meaningful while running).
        if (e.PropertyName is nameof(VmItemViewModel.Status) or nameof(VmItemViewModel.CanStart))
        {
            if (_dispatcherQueue is { } dispatcher)
            {
                dispatcher.TryEnqueue(() =>
                {
                    Bindings.Update();
                    _ = RefreshLiveRowsAsync();
                });
            }
            else
            {
                Bindings.Update();
                _ = RefreshLiveRowsAsync();
            }
        }
    }

    private void Rebuild()
    {
        _accelerationNoticeDismissed = false;
        NetworkRows.Clear();
        DriveRows.Clear();
        Terminal.Endpoint = Vm?.SerialPort ?? 0;

        if (Vm is not { } vm)
        {
            RefreshAccelerationNotice();
            Bindings.Update();
            return;
        }

        foreach (var drive in vm.Bundle.Configuration.Drives)
        {
            DriveRows.Add(new DetailRow(
                DriveLabel(drive),
                drive.IsExternal ? "External image" : drive.ImageName ?? "—"));
        }

        for (var i = 0; i < vm.Bundle.Configuration.Networks.Count; i++)
        {
            var network = vm.Bundle.Configuration.Networks[i];
            NetworkRows.Add(new DetailRow(
                $"Adapter {i + 1}",
                $"{network.Mode} · {network.Hardware}"));
        }

        if (DriveRows.Count == 0)
        {
            DriveRows.Add(new DetailRow("No drives", "—"));
        }

        if (NetworkRows.Count == 0)
        {
            NetworkRows.Add(new DetailRow("No adapters", "—"));
        }

        RefreshAccelerationNotice();
        Bindings.Update();
        _ = RefreshLiveRowsAsync();
    }

    private bool _accelerationNoticeDismissed;
    private bool _enablingAcceleration;

    /// <summary>
    /// Shows the acceleration notice when a same-architecture guest falls back to
    /// TCG on this host, offering the elevated one-click enablement while the
    /// Windows Hypervisor Platform feature itself is off. Never shown for
    /// cross-architecture guests, where TCG is the only option by design.
    /// </summary>
    private void RefreshAccelerationNotice()
    {
        if (_enablingAcceleration)
        {
            return; // the bar is showing enablement progress — don't clobber it
        }

        if (Vm is not { } vm || _accelerationNoticeDismissed)
        {
            AccelerationBar.IsOpen = false;
            return;
        }

        if (!AcceleratorDetector.CanGuestMatchHostArchitecture(vm.Architecture))
        {
            AccelerationBar.IsOpen = false;
            return;
        }

        var status = WhpFeature.GetStatus();
        if (status == WhpFeatureStatus.Available)
        {
            AccelerationBar.IsOpen = false;
            return;
        }

        var canEnable = WhpFeature.CanOfferEnablement(status);
        AccelerationBar.Message = WhpFeature.GetGuidance(status);
        AccelerationBar.Severity = canEnable ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        EnableAccelerationButton.Visibility = canEnable ? Visibility.Visible : Visibility.Collapsed;
        AccelerationBar.IsOpen = true;
    }

    private void OnAccelerationBarCloseClick(InfoBar sender, object args) => _accelerationNoticeDismissed = true;

    private async void OnEnableAccelerationClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || _enablingAcceleration)
        {
            return;
        }

        var confirm = new ContentDialog
        {
            Title = "Enable Windows Hypervisor Platform?",
            Content = "ZuTM runs 'dism /Online /Enable-Feature /FeatureName:HypervisorPlatform /All /NoRestart' "
                + "in an elevated process. Windows asks for administrator consent and shows the DISM progress in a "
                + "console window. Afterwards Windows must be restarted, and hardware acceleration applies from the "
                + "next start of this VM.",
            PrimaryButtonText = "Enable",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _enablingAcceleration = true;
        EnableAccelerationButton.IsEnabled = false;
        EnableAccelerationProgress.IsActive = true;
        EnableAccelerationProgress.Visibility = Visibility.Visible;
        AccelerationBar.Message = "Waiting for administrator consent, then enabling the feature via DISM…";
        WhpEnableResult result;
        try
        {
            result = await Task.Run(WhpFeature.EnableElevated);
        }
        finally
        {
            _enablingAcceleration = false;
            EnableAccelerationProgress.IsActive = false;
            EnableAccelerationProgress.Visibility = Visibility.Collapsed;
            EnableAccelerationButton.IsEnabled = true;
        }

        switch (result.Outcome)
        {
            case WhpEnableOutcome.UserCancelled:
                RefreshAccelerationNotice(); // restore the advisory message
                break;
            case WhpEnableOutcome.Failed:
                AccelerationBar.Severity = InfoBarSeverity.Error;
                AccelerationBar.Message = $"Enabling Windows Hypervisor Platform failed (DISM exit code {result.ExitCode}). "
                    + "To enable it manually, run optionalfeatures.exe, tick 'Windows Hypervisor Platform', "
                    + "and restart Windows.";
                break;
            default:
                _accelerationNoticeDismissed = false;
                RefreshAccelerationNotice(); // now classifies as hypervisor-inactive → restart Windows
                break;
        }
    }

    // -- Live removable media & USB routing ---------------------------------------

    private bool _refreshingLiveRows;

    /// <summary>Re-reads CD trays and guest-routed USB devices from the running VM.</summary>
    private async Task RefreshLiveRowsAsync()
    {
        if (Vm is not { } vm || Library is null || !CanHotPlug || _refreshingLiveRows)
        {
            Bindings.Update();
            return;
        }

        _refreshingLiveRows = true;
        try
        {
            var trays = await Library.GetCdTraysAsync(vm);
            var attached = await Library.GetAttachedUsbDevicesAsync(vm);
            var hostDevices = await Library.ListUsbHostDevicesAsync();

            MediaRows.Clear();
            foreach (var tray in trays)
            {
                MediaRows.Add(new CdTrayRow(tray));
            }

            var attachedById = attached.ToDictionary(a => a.DeviceId);
            UsbRows.Clear();
            foreach (var device in hostDevices.OrderBy(d => d.Name, StringComparer.CurrentCulture))
            {
                UsbRows.Add(new UsbHostRow(device, attachedById.ContainsKey(device.DeviceId)));
            }

            Bindings.Update();
        }
        catch (Exception ex) when (ex is InvalidOperationException or QmpException)
        {
            // The VM stopped mid-refresh — the sections hide via the status binding.
        }
        finally
        {
            _refreshingLiveRows = false;
        }
    }

    private async void OnMountIsoClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Library is null)
        {
            return;
        }

        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        if (App.MainWindow is { } window)
        {
            // Unpackaged WinUI 3 pickers must be bound to a window handle.
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        }

        picker.FileTypeFilter.Add(".iso");
        picker.FileTypeFilter.Add(".img");
        picker.FileTypeFilter.Add(".bin");

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            await Library.MountIsoAsync(vm, file.Path);
        }
        catch (Exception ex)
        {
            vm.Error = $"Mount failed: {ex.Message}";
        }

        await RefreshLiveRowsAsync();
    }

    private async void OnTrayEjectClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CdTrayRow row
            || Vm is not { } vm || Library is null)
        {
            return;
        }

        try
        {
            await Library.EjectIsoAsync(vm, row.Tray);
        }
        catch (Exception ex)
        {
            vm.Error = $"Eject failed: {ex.Message}";
        }

        await RefreshLiveRowsAsync();
    }

    private void OnUsbRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshLiveRowsAsync();

    private async void OnUsbAttach(object sender, RoutedEventArgs e)
    {
        // Programmatic realization of the list also raises Checked — only react
        // to user toggles that actually change the row's state.
        if ((sender as FrameworkElement)?.DataContext is not UsbHostRow { IsAttached: false } row
            || Vm is not { } vm || Library is null)
        {
            return;
        }

        try
        {
            await Library.AttachUsbDeviceAsync(vm, row.Device);
        }
        catch (Exception ex)
        {
            vm.Error = $"USB routing failed: {ex.Message}";
        }

        await RefreshLiveRowsAsync();
    }

    private async void OnUsbDetach(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not UsbHostRow { IsAttached: true } row
            || Vm is not { } vm || Library is null)
        {
            return;
        }

        try
        {
            await Library.DetachUsbDeviceAsync(vm, row.Device.DeviceId);
        }
        catch (Exception ex)
        {
            vm.Error = $"USB detach failed: {ex.Message}";
        }

        await RefreshLiveRowsAsync();
    }

    private static string DriveLabel(UtmDrive drive) => drive.ImageType switch
    {
        UtmValues.DriveImageType.Disk => "Disk",
        UtmValues.DriveImageType.Cd => "CD/DVD",
        UtmValues.DriveImageType.Bios => "BIOS",
        UtmValues.DriveImageType.LinuxKernel => "Kernel",
        UtmValues.DriveImageType.LinuxInitrd => "Initrd",
        UtmValues.DriveImageType.LinuxDtb => "Device tree",
        _ => drive.ImageType,
    };

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && StartRequested is not null)
        {
            await StartRequested(this, Vm);
        }
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && StopRequested is not null)
        {
            await StopRequested(this, Vm);
        }
    }

    private async void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && PauseRequested is not null)
        {
            await PauseRequested(this, Vm);
        }
    }

    private async void OnResumeClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && ResumeRequested is not null)
        {
            await ResumeRequested(this, Vm);
        }
    }

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && ResetRequested is not null)
        {
            await ResetRequested(this, Vm);
        }
    }

    private async void OnCloneClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && CloneRequested is not null)
        {
            await CloneRequested(this, Vm);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && DeleteRequested is not null)
        {
            await DeleteRequested(this, Vm);
        }
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        var dialog = new VMConfigEditorDialog(
            Vm.Bundle,
            Library is null ? null : () => Library.ListUsbHostDevicesAsync(),
            () => Library?.GetUsbRouting(Vm))
        { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Rebuild(); // fields may have changed
        }
    }

    private async void OnEjectIsoClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not { CanStart: true } vm)
        {
            return; // only while stopped — QEMU holds the ISO open while running
        }

        var confirm = new ContentDialog
        {
            Title = "Eject installer ISO?",
            Content = "The CD drive is detached and the ISO copy inside the VM bundle is deleted. "
                + "On the next start the VM boots from its disk.",
            PrimaryButtonText = "Eject",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                vm.Bundle.DetachCdDrives();
                vm.Bundle.Save();
            });
        }
        catch (Exception ex)
        {
            vm.Error = $"Eject failed: {ex.Message}";
        }

        Rebuild();
    }

    private async void OnSnapshotClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && SnapshotRequested is not null)
        {
            await SnapshotRequested(this, Vm);
        }
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && RestoreRequested is not null)
        {
            await RestoreRequested(this, Vm);
        }
    }

    public event Func<object, VmItemViewModel, Task>? StartRequested;

    public event Func<object, VmItemViewModel, Task>? StopRequested;

    public event Func<object, VmItemViewModel, Task>? PauseRequested;

    public event Func<object, VmItemViewModel, Task>? ResumeRequested;

    public event Func<object, VmItemViewModel, Task>? ResetRequested;

    public event Func<object, VmItemViewModel, Task>? CloneRequested;

    public event Func<object, VmItemViewModel, Task>? DeleteRequested;

    public event Func<object, VmItemViewModel, Task>? SnapshotRequested;

    public event Func<object, VmItemViewModel, Task>? RestoreRequested;
}
