// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZuTM.App.Services;
using ZuTM.App.ViewModels;
using ZuTM.Core.Utm;

namespace ZuTM.App;

public sealed partial class VmDetailView : UserControl
{
    public static readonly DependencyProperty VmProperty = DependencyProperty.Register(
        nameof(Vm), typeof(VmItemViewModel), typeof(VmDetailView), new PropertyMetadata(null, OnVmChanged));

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

    public VmDetailView()
    {
        InitializeComponent();
        DataContext = this;
    }

    private static void OnVmChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((VmDetailView)sender).Rebuild();

    private void Rebuild()
    {
        NetworkRows.Clear();
        DriveRows.Clear();

        if (Vm is not { } vm)
        {
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

        Bindings.Update();
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

    public event Func<object, VmItemViewModel, Task>? StartRequested;

    public event Func<object, VmItemViewModel, Task>? StopRequested;

    public event Func<object, VmItemViewModel, Task>? PauseRequested;

    public event Func<object, VmItemViewModel, Task>? ResumeRequested;

    public event Func<object, VmItemViewModel, Task>? ResetRequested;

    public event Func<object, VmItemViewModel, Task>? CloneRequested;

    public event Func<object, VmItemViewModel, Task>? DeleteRequested;
}
