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
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SizeChanged += OnWindowSizeChanged;
        ApplyMica();
        SizeToSaneDefault();
        ApplyMinimumSize();
        RestorePlacement();
        TrackPlacement();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

        var dispatcher = DispatcherQueue;
        var library = new VmLibraryService(InvokeOnUiAsync);
        ViewModel = new MainViewModel(library);
        DetailView.Library = library;
        DetailView.Operations = new VmActivityService(library, InvokeOnUiAsync);
        library.ActivitiesChanged += () => dispatcher.TryEnqueue(UpdateStatus);

        DetailView.StartRequested += async (_, vm) => await library.StartAsync(vm);
        DetailView.StopRequested += async (_, vm) => await library.StopAsync(vm);
        DetailView.PauseRequested += async (_, vm) => await library.PauseAsync(vm);
        DetailView.ResumeRequested += async (_, vm) => await library.ResumeAsync(vm);
        DetailView.ResetRequested += async (_, vm) => await library.ResetAsync(vm);
        DetailView.CloneRequested += async (_, vm) => await CloneVmAsync(vm);
        DetailView.DeleteRequested += async (_, vm) => await DeleteVmAsync(vm);
        DetailView.SnapshotRequested += async (_, vm) => await SnapshotVmAsync(vm);
        DetailView.RestoreRequested += async (_, vm) => await RestoreVmAsync(vm);
        DetailView.CreateRequested += (_, _) => OnNewVmClick(this, new RoutedEventArgs());

        _transientTimer.Tick += (_, _) =>
        {
            _transientTimer.Stop();
            TransientBar.IsOpen = false;
        };

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedVm))
            {
                DetailView.Vm = ViewModel.SelectedVm;
            }

            // UpdateStatus() clears and refills the bound VM list. The list's
            // TwoWay SelectedItem binding writes back while a change is still
            // propagating, so running this synchronously inside the
            // notification re-enters ObservableCollection (CheckReentrancy
            // throws, XAML stows the exception, the process fail-fasts —
            // seen as a hard crash right after creating a VM).
            dispatcher.TryEnqueue(UpdateStatus);
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

    // The two-pane layout crushes below this; enforced as a presenter minimum.
    private const int MinWindowWidth = 860;
    private const int MinWindowHeight = 560;

    private void ApplyMinimumSize()
    {
        if (AppWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = MinWindowWidth;
            presenter.PreferredMinimumHeight = MinWindowHeight;
        }
    }

    private SizeInt32? _lastNormalSize;
    private PointInt32? _lastNormalPosition;
    private bool _wasMaximized;

    private void RestorePlacement()
    {
        if (AppWindow is null)
        {
            return;
        }

        // Runs before the ViewModel exists — read the persisted settings
        // directly (the library applies the same file later in the ctor).
        var settings = AppSettings.Load();
        if (settings.WindowLeft is { } left && settings.WindowTop is { } top
            && settings.WindowWidth is { } width && settings.WindowHeight is { } height
            && width >= MinWindowWidth && height >= MinWindowHeight)
        {
            // Only restore onto a display that still exists (unplugged monitor
            // would strand the window off-screen).
            if (Microsoft.UI.Windowing.DisplayArea.GetFromPoint(
                    new PointInt32(left, top), Microsoft.UI.Windowing.DisplayAreaFallback.None)
                is not null)
            {
                AppWindow.MoveAndResize(new RectInt32(left, top, width, height));
            }
        }

        if (settings.WindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private void TrackPlacement()
    {
        if (AppWindow is null)
        {
            return;
        }

        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange)
            {
                var presenter = AppWindow.Presenter as OverlappedPresenter;
                var maximized = presenter?.State == OverlappedPresenterState.Maximized;
                if (!maximized)
                {
                    // DPI-scaled logical values — the same units MoveAndResize takes.
                    _lastNormalSize = AppWindow.Size;
                    _lastNormalPosition = AppWindow.Position;
                }

                _wasMaximized = maximized;
            }
        };

        Closed += (_, _) =>
        {
            if (_lastNormalSize is not { } size || _lastNormalPosition is not { } position)
            {
                return;
            }

            try
            {
                ViewModel.Library.SaveSettings(ViewModel.Library.Settings with
                {
                    WindowLeft = position.X,
                    WindowTop = position.Y,
                    WindowWidth = size.Width,
                    WindowHeight = size.Height,
                    WindowMaximized = _wasMaximized,
                });
            }
            catch (Exception)
            {
                // Placement persistence is pure polish — never block shutdown.
            }
        };
    }

    private List<VmItemViewModel> _allVms = [];

    private void OnSearchChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
    {
        // Typing in the filter must not throw away the user's selection —
        // the keep-selection variant re-selects when the VM still matches,
        // and the ListView's TwoWay binding clears it when it doesn't.
        ApplyFilterKeepSelection();
        UpdateStatus();
    }

    private void ApplyFilterKeepSelection()
    {
        var selected = ViewModel.SelectedVm;
        var query = SearchBox?.Text?.Trim() ?? string.Empty;
        ViewModel.VirtualMachines.Clear();
        foreach (var vm in _allVms
                     .Where(vm => query.Length == 0 || vm.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(vm => vm.Name, StringComparer.CurrentCulture))
        {
            ViewModel.VirtualMachines.Add(vm);
        }

        if (selected is not null && ViewModel.VirtualMachines.Contains(selected))
        {
            ViewModel.SelectedVm = selected;
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox?.Text?.Trim() ?? string.Empty;
        ViewModel.SelectedVm = null;
        DetailView.Vm = null;
        ViewModel.VirtualMachines.Clear();
        foreach (var vm in _allVms
                     .Where(vm => query.Length == 0 || vm.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(vm => vm.Name, StringComparer.CurrentCulture))
        {
            ViewModel.VirtualMachines.Add(vm);
        }
    }

    private bool _updatingStatus;

    private void UpdateStatus()
    {
        // A queued UpdateStatus can still land inside another one's
        // collection churn (selection write-back lands on the dispatcher
        // too); a nested run would Clear() mid-notification again.
        if (_updatingStatus)
        {
            _statusRefreshQueued = true;
            return;
        }

        _updatingStatus = true;
        try
        {
            _allVms = [.. ViewModel.Library.VirtualMachines];
            ApplyFilterKeepSelection();
            var running = ViewModel.Library.RunningCount();
            var active = ViewModel.Library.VirtualMachines
                .Where(vm => vm.Activity is not null)
                .Select(vm => vm.Activity!)
                .ToList();
            var activityText = active.Count == 0
                ? string.Empty
                : $" · {active.Count} active: {active[0].Summary}"
                    + (active.Count > 1 ? $" (+{active.Count - 1} more)" : string.Empty);
            StatusBarText.Text = ViewModel.VirtualMachines.Count == 0
                ? "No virtual machines yet — click “New VM” to create one."
                : $"{ViewModel.VirtualMachines.Count} VM(s) · {running} running · folder: {ViewModel.Library.Settings.VmFolder}{activityText}";
        }
        finally
        {
            _updatingStatus = false;
            if (_statusRefreshQueued)
            {
                _statusRefreshQueued = false;
                DispatcherQueue.TryEnqueue(UpdateStatus);
            }
        }
    }

    private bool _statusRefreshQueued;

    private void OnRefreshClick(object sender, RoutedEventArgs e) => ViewModel.ReloadCommand.Execute(null);

    private void OnFocusSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }    private async void OnDeleteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // The Delete key means "delete this VM" only while the list has
        // focus — and routes through the same confirmation as the button.
        if (ViewModel.SelectedVm is { CanDelete: true } vm)
        {
            await DeleteVmAsync(vm);
            args.Handled = true;
        }
    }

    private async Task CloneVmAsync(VmItemViewModel vm)
    {
        // Progress, cancellation and failure surfacing live on the VM's
        // activity; nothing here blocks in a modal.
        var clone = await DetailView.Operations!.CloneAsync(vm);
        if (clone is null)
        {
            return;
        }

        UpdateStatus();
        ShowTransient($"Cloned “{vm.Name}” → “{clone.Name}”.");
    }

    private async Task DeleteVmAsync(VmItemViewModel vm)
    {
        var dialog = new ContentDialog
        {
            Title = $"Delete “{vm.Name}”?",
            Content = "This permanently removes the VM and every disk inside its bundle. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var deletedId = vm.Id;
            await ViewModel.Library.DeleteAsync(vm);
            if (ViewModel.SelectedVm?.Id == deletedId)
            {
                ViewModel.SelectedVm = null;
                DetailView.Vm = null;
            }

            UpdateStatus();
        }
        catch (Exception ex)
        {
            ShowError($"Delete failed: {ex.Message}");
        }
    }

    private async Task SnapshotVmAsync(VmItemViewModel vm)
    {
        if (vm.Process?.Qmp is not { } qmp)
        {
            return;
        }

        var nameBox = new TextBox { PlaceholderText = "snapshot name (e.g. clean-state)" };
        var dialog = new ContentDialog
        {
            Title = $"Snapshot “{vm.Name}”",
            Content = nameBox,
            PrimaryButtonText = "Save snapshot",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var name = nameBox.Text.Trim().Replace(' ', '-');
        if (name.Length == 0)
        {
            name = $"snap-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        }

        try
        {
            // savevm pauses internally; surface the frozen state while it runs.
            // No progress events exist and the write cannot be interrupted —
            // the activity says so honestly (no cancel button).
            vm.Activity = new VmActivity(
                VmActivityKind.SavingSnapshot, $"Saving snapshot “{name}”", canPause: false, canCancel: false);
            try
            {
                await qmp.SaveSnapshotAsync(name);
                ShowTransient($"Snapshot “{name}” saved.");
            }
            finally
            {
                vm.Activity = null;
            }
        }
        catch (Exception ex)
        {
            ShowError($"Snapshot failed: {ex.Message}");
        }
    }

    private async Task RestoreVmAsync(VmItemViewModel vm)
    {
        if (vm.Process?.Qmp is not { } qmp)
        {
            return;
        }

        IReadOnlyList<string> snapshots;
        try
        {
            snapshots = await qmp.ListSnapshotsAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Could not list snapshots: {ex.Message}");
            return;
        }

        if (snapshots.Count == 0)
        {
            ShowTransient("This VM has no snapshots yet.", InfoBarSeverity.Informational);
            return;
        }

        var combo = new ComboBox { ItemsSource = snapshots.ToList(), SelectedIndex = 0, MinWidth = 240 };
        var dialog = new ContentDialog
        {
            Title = $"Restore “{vm.Name}”",
            Content = combo,
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || combo.SelectedItem is not string snapshot)
        {
            return;
        }

        try
        {
            // loadvm requires the guest stopped: freeze → load → continue.
            vm.Activity = new VmActivity(
                VmActivityKind.RestoringSnapshot, $"Restoring snapshot “{snapshot}”", canPause: false, canCancel: false);
            try
            {
                await qmp.PauseAsync();
                await qmp.LoadSnapshotAsync(snapshot);
                await qmp.ResumeAsync();
                ShowTransient($"Snapshot “{snapshot}” restored.");
            }
            finally
            {
                vm.Activity = null;
            }
        }
        catch (Exception ex)
        {
            ShowError($"Restore failed: {ex.Message}");
        }
    }

    private readonly DispatcherTimer _transientTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    /// <summary>Non-blocking feedback — replaces "ZuTM"-titled modal dialogs.</summary>
    private void ShowTransient(string message, InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        TransientBar.Severity = severity;
        TransientBar.Message = message;
        TransientBar.IsOpen = true;
        _transientTimer.Stop();
        _transientTimer.Start();
    }

    /// <summary>Sticky, dismissible error feedback for operations that did not succeed.</summary>
    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private async void OnVmDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedVm is { CanStart: true } vm)
        {
            await ViewModel.Library.StartAsync(vm);
        }
    }

    // -- Pane collapse (adaptive at narrow widths) ---------------------------------

    private const double PaneExpandedWidth = 300;
    private const double AutoCollapseBelowWidth = 720;
    private bool _paneCollapsedByUser;

    private bool PaneCollapsed
    {
        get => PaneColumn.Width.GridUnitType == GridUnitType.Pixel && PaneColumn.Width.Value == 0;
        set
        {
            PaneColumn.Width = new GridLength(value ? 0 : PaneExpandedWidth, GridUnitType.Pixel);
            PaneToggleButton.IsEnabled = !value;
            PaneExpandButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnPaneToggleClick(object sender, RoutedEventArgs e)
    {
        _paneCollapsedByUser = !PaneCollapsed;
        PaneCollapsed = _paneCollapsedByUser;
    }

    private void OnWindowSizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        // Narrow windows auto-collapse the pane so the detail pane keeps room;
        // widening restores whatever the user chose.
        if (args.Size.Width < AutoCollapseBelowWidth)
        {
            if (!PaneCollapsed)
            {
                PaneCollapsed = true;
            }
        }
        else if (PaneCollapsed && !_paneCollapsedByUser)
        {
            PaneCollapsed = false;
        }
    }

    // -- List context menu --------------------------------------------------------

    private static VmItemViewModel? VmFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as VmItemViewModel;

    private async void OnMenuStartClick(object sender, RoutedEventArgs e)
    {
        if (VmFrom(sender) is { } vm)
        {
            ViewModel.SelectedVm = vm;
            await ViewModel.Library.StartAsync(vm);
        }
    }

    private async void OnMenuStopClick(object sender, RoutedEventArgs e)
    {
        if (VmFrom(sender) is { } vm)
        {
            ViewModel.SelectedVm = vm;
            await ViewModel.Library.StopAsync(vm);
        }
    }

    private async void OnMenuCloneClick(object sender, RoutedEventArgs e)
    {
        if (VmFrom(sender) is { } vm)
        {
            ViewModel.SelectedVm = vm;
            await CloneVmAsync(vm);
        }
    }

    private async void OnMenuDeleteClick(object sender, RoutedEventArgs e)
    {
        if (VmFrom(sender) is { } vm)
        {
            ViewModel.SelectedVm = vm;
            await DeleteVmAsync(vm);
        }
    }

    private async void OnMenuOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (VmFrom(sender) is { } vm)
        {
            _ = Windows.System.Launcher.LaunchFolderPathAsync(vm.Bundle.BundlePath);
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
            // CreateAsync already added the VM to the library — a reload here
            // would replace the wrapper carrying the new activity.
            ViewModel.SelectedVm = ViewModel.VirtualMachines.FirstOrDefault(v => v.Id == dialog.CreatedVm.Id);
            if (dialog.CreationPlan is { } plan)
            {
                // Slow work (ISO fetch/import, disk create) runs on the row,
                // not behind this dialog.
                _ = DetailView.Operations!.RunCreationAsync(dialog.CreatedVm, plan);
            }

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
                    Content = "ZuTM hides itself while Windows Installer runs and comes back "
                        + "with an explanation if the update does not go through.",
                    PrimaryButtonText = "Install update",
                    CloseButtonText = "Later",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot,
                };
                if (await proceed.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                // Hide (don't close): if the install fails or the user cancels
                // UAC, ZuTM must still be there to say so — a closed window on
                // a failed update was exactly the silent failure this replaces.
                AppWindow.Hide();
                UpdateInstallResult result;
                try
                {
                    result = await UpdateInstallRunner.InstallAsync(installerPath);
                }
                catch (Exception ex)
                {
                    AppWindow.Show();
                    Activate();
                    ShowError("The update could not be started: " + ex.Message);
                    return;
                }

                switch (result.Outcome)
                {
                    case InstallerOutcome.Success:
                        // Replaced in place — exit so the new install takes over.
                        Close();
                        return;
                    case InstallerOutcome.SuccessRebootRequired:
                        Close(); // new version is in place; Windows finishes it on reboot
                        return;
                    case InstallerOutcome.UserCancelled:
                        AppWindow.Show();
                        Activate();
                        ShowTransient("Update cancelled — ZuTM is still running your current version.", InfoBarSeverity.Informational);
                        return;
                    default:
                        AppWindow.Show();
                        Activate();
                        ShowError(
                            $"The update failed to install (Windows Installer exit code {result.ExitCode}). "
                            + "Your current ZuTM is unchanged. A detailed log (MSI*.log) is in your %TEMP% folder.");
                        return;
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
