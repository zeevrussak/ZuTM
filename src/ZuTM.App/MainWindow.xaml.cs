// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ZuTM.App.Services;
using ZuTM.App.ViewModels;
using ZuTM.Core.Qemu;
using ZuTM.Update;
using Windows.Graphics;

namespace ZuTM.App;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    private UpdateViewModel? _updateViewModel;

    public MainWindow()
    {
        InitializeComponent();

        Title = "ZuTM — Virtual Machines";
        ApplyMica();
        SizeToSaneDefault();

        var dispatcher = DispatcherQueue;
        var library = new VmLibraryService(InvokeOnUiAsync);
        ViewModel = new MainViewModel(library);

        DetailView.StartRequested += async (_, vm) => await library.StartAsync(vm);
        DetailView.StopRequested += async (_, vm) => await library.StopAsync(vm);

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedVm))
            {
                DetailView.Vm = ViewModel.SelectedVm;
            }

            UpdateStatus();
        };
        VmFolderText.Text = library.Settings.VmFolder;
        UpdateStatus();

        _ = ViewModel.LoadAsync();
        _ = MaybeCheckForUpdatesAsync();
    }

    private Task InvokeOnUiAsync(Func<Task> work)
    {
        var completion = new TaskCompletionSource();
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await work();
            }
            finally
            {
                completion.SetResult();
            }
        }))
        {
            completion.SetResult();
        }

        return completion.Task;
    }

    private void ApplyMica()
    {
        if (!MicaController.IsSupported())
        {
            return;
        }

        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
    }

    private void SizeToSaneDefault()
    {
        if (AppWindow is null)
        {
            return;
        }

        AppWindow.Resize(new SizeInt32(1180, 760));
    }

    private void UpdateStatus()
    {
        var running = ViewModel.Library.RunningCount();
        StatusBarText.Text = ViewModel.VirtualMachines.Count == 0
            ? "No virtual machines yet — click “New VM” to create one."
            : $"{ViewModel.VirtualMachines.Count} VM(s) · {running} running · folder: {ViewModel.Library.Settings.VmFolder}";
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => ViewModel.ReloadCommand.Execute(null);

    private async void OnVmDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedVm is { CanStart: true } vm)
        {
            await ViewModel.Library.StartAsync(vm);
        }
    }

    private async void OnNewVmClick(object sender, RoutedEventArgs e)
    {
        var dialog = new NewVmDialog(ViewModel.Library)
        {
            XamlRoot = Content.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.CreatedVm is not null)
        {
            await ViewModel.ReloadAsync();
            ViewModel.SelectedVm = ViewModel.VirtualMachines.FirstOrDefault(v => v.Id == dialog.CreatedVm.Id);
            UpdateStatus();
        }
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        _updateViewModel ??= new UpdateViewModel(
            new UpdateChecker(new GitHubReleaseFeed(ViewModel.Library.Settings.UpdateRepository)),
            new UpdateInstaller(new HttpClient()))
        {
            InstallHandoff = async installerPath =>
            {
                var proceed = new ContentDialog
                {
                    Title = "Install update?",
                    Content = "ZuTM will close and Windows Installer will upgrade it in place.",
                    PrimaryButtonText = "Install and restart",
                    CloseButtonText = "Later",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot,
                };
                if (await proceed.ShowAsync() == ContentDialogResult.Primary)
                {
                    UpdateInstaller.StartInstaller(installerPath);
                    Close();
                }
            },
        };

        var dialog = new SettingsDialog(ViewModel.Library, _updateViewModel)
        {
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
        VmFolderText.Text = ViewModel.Library.Settings.VmFolder;
        UpdateStatus();
    }

    private void OnDetailLoaded(object sender, RoutedEventArgs e)
    {
        // Selection wiring happens in the PropertyChanged handler; nothing to do here.
    }

    private async Task MaybeCheckForUpdatesAsync()
    {
        var settings = ViewModel.Library.Settings;
        if (!settings.CheckForUpdates)
        {
            return;
        }

        var stale = settings.LastUpdateCheckUtc is not { } last || DateTimeOffset.UtcNow - last > TimeSpan.FromDays(7);
        if (!stale)
        {
            return;
        }

        var updateViewModel = _updateViewModel ??= new UpdateViewModel(
            new UpdateChecker(new GitHubReleaseFeed(settings.UpdateRepository)),
            new UpdateInstaller(new HttpClient()));
        await updateViewModel.CheckAsync();
        if (updateViewModel.Result?.UpdateAvailable == true)
        {
            await InvokeOnUiAsync(async () =>
            {
                var dialog = new ContentDialog
                {
                    Title = $"ZuTM {updateViewModel.Result.LatestVersion} is available",
                    Content = "Open Settings ▸ Updates to download and install it.",
                    PrimaryButtonText = "Open Settings",
                    CloseButtonText = "Not now",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot,
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    OnSettingsClick(this, new RoutedEventArgs());
                }
            });
        }

        ViewModel.Library.SaveSettings(settings with { LastUpdateCheckUtc = DateTimeOffset.UtcNow });
    }
}
