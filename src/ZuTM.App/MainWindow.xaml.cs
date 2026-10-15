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

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        VmFolderText.Text = library.Settings.VmFolder;
        ApplyTheme(library.Settings.Theme);
        UpdateStatus();

        // Ctrl+, can't be written in XAML: the runtime parser rejects numeric
        // key codes and the compiler rejects the name "Comma".
        SettingsButton.KeyboardAccelerators.Add(new KeyboardAccelerator
        {
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
            Key = (Windows.System.VirtualKey)0xBC, // VK_OEM_COMMA
        });
        // Close is two-phase on purpose: WinUI processes WM_CLOSE nested inside
        // whatever queued callback is running, and tearing the XAML island down
        // under a live callback makes every DispatcherQueue.TryEnqueue from
        // that callback block for the whole teardown (measured 15-60s). So the
        // first close request is cancelled, the window is hidden (callbacks
        // drain safely against a hidden window), background operations are
        // cancelled, and only then is Close issued on a quiet dispatcher.
        AppWindow.Closing += (_, args) =>
        {
            if (_closing)
            {
                return; // our own deferred Close — let it proceed
            }

            args.Cancel = true;
            _closing = true;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            AppWindow.Hide();
            ShutdownAndCloseAsync();
        };

        Closed += (_, _) => _closing = true;

        _ = ViewModel.LoadAsync();
        _ = MaybeCheckForUpdatesAsync();
    }

    private async void ShutdownAndCloseAsync()
    {
        try
        {
            await DetailView.Operations!.CancelAllAsync();
        }
        catch (Exception)
        {
            // Shutdown cleanup is best-effort by design.
        }

        await Task.Delay(250); // let already-queued callbacks drain on the hidden window
        DispatcherQueue.TryEnqueue(Close);
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
            if (!ReferenceEquals(ViewModel.SelectedVm, selected))
            {
                // Setting Selector.SelectedItem mid-collection-mutation makes
                // XAML re-resolve selection across container generation
                // (measured: multi-second UI stalls). Defer until the list
                // and its containers have settled.
                DispatcherQueue.TryEnqueue(() => ViewModel.SelectedVm = selected);
            }
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

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedVm))
        {
            // The SelectedVm notification can fire from any thread — and from
            // inside UpdateStatus's own list rebuild via the ListView's TwoWay
            // write-back. A synchronous DetailView.Vm set there runs a full
            // DP/ComWrappers cascade mid-churn (measured: multi-second UI
            // stalls), so the assignment is always deferred. During close the
            // handler is unsubscribed before this can ever run.
            DispatcherQueue.TryEnqueue(() => DetailView.Vm = ViewModel.SelectedVm);
        }

        // UpdateStatus() clears and refills the bound VM list. The list's
        // TwoWay SelectedItem binding writes back while a change is still
        // propagating, so running this synchronously inside the
        // notification re-enters ObservableCollection (CheckReentrancy
        // throws, XAML stows the exception, the process fail-fasts —
        // seen as a hard crash right after creating a VM).
        DispatcherQueue.TryEnqueue(UpdateStatus);
    }

    private bool _updatingStatus;
    private bool _closing;

    private void UpdateStatus()
    {
        // Callbacks queued before Closed still fire during teardown; touching
        // the list then deadlocks against XAML island destruction.
        if (_closing)
        {
            return;
        }
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
        if (await ShowDialogSafeAsync(dialog) != ContentDialogResult.Primary)
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
        if (await ShowDialogSafeAsync(dialog) != ContentDialogResult.Primary)
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
        if (await ShowDialogSafeAsync(dialog) != ContentDialogResult.Primary || combo.SelectedItem is not string snapshot)
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
    public void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    /// <summary>Applies the persisted theme choice ("system", "light", "dark") to the UI tree.</summary>
    public void ApplyTheme(string theme) =>
        RootGrid.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

    // -- Drag & drop (.utm import, .iso mount) --------------------------------------

    private const string StorageItemsFormat = "StorageItems"; // StandardDataTypes.StorageItems

    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        var hasUsableItem = e.DataView.Contains(StorageItemsFormat);
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Drop a .utm bundle to import it, or an .iso to mount it into the running VM";
        e.DragUIOverride.IsCaptionVisible = true;
        e.Handled = hasUsableItem;
    }

    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (!e.DataView.Contains(StorageItemsFormat))
            {
                return;
            }

            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items)
            {
                switch (item)
                {
                    case Windows.Storage.StorageFolder { Path: { Length: > 0 } folderPath }
                        when folderPath.EndsWith(".utm", StringComparison.OrdinalIgnoreCase):
                        await ImportDroppedBundleAsync(folderPath);
                        break;
                    case Windows.Storage.StorageFile { Path: { Length: > 0 } filePath }
                        when filePath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase):
                        await MountDroppedIsoAsync(filePath);
                        break;
                }
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task ImportDroppedBundleAsync(string bundlePath)
    {
        var target = Path.Combine(
            ViewModel.Library.Settings.VmFolder,
            Path.GetFileName(bundlePath.TrimEnd(Path.DirectorySeparatorChar)));
        if (string.Equals(
                Path.GetFullPath(bundlePath.TrimEnd(Path.DirectorySeparatorChar)),
                Path.GetFullPath(target),
                StringComparison.OrdinalIgnoreCase))
        {
            ShowTransient("That bundle is already in the VM folder.", InfoBarSeverity.Informational);
            return;
        }

        if (Directory.Exists(target))
        {
            ShowError($"A VM named “{Path.GetFileNameWithoutExtension(target)}” already exists.");
            return;
        }

        try
        {
            await Task.Run(() => CopyDirectory(bundlePath, target));
            await ViewModel.ReloadAsync();
            UpdateStatus();
            ShowTransient($"Imported “{Path.GetFileNameWithoutExtension(target)}”.");
        }
        catch (Exception ex)
        {
            ShowError($"Import failed: {ex.Message}");
        }
    }

    private async Task MountDroppedIsoAsync(string isoPath)
    {
        if (ViewModel.SelectedVm is not { } vm)
        {
            ShowError("Select a VM first — an .iso mounts into the selected running VM.");
            return;
        }

        if (vm.Status is not (VmStatus.Running or VmStatus.Paused))
        {
            ShowError("Start the VM first — a dropped .iso mounts through the live CD tray (QMP).");
            return;
        }

        try
        {
            await ViewModel.Library.MountIsoAsync(vm, isoPath);
            ShowTransient($"Mounted {Path.GetFileName(isoPath)} into “{vm.Name}”.");
        }
        catch (Exception ex)
        {
            ShowError($"Mount failed: {ex.Message}");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    
    /// <summary>
    /// ContentDialogs are single-instance: showing one while another is still
    /// finishing its close animation throws and (pre-fix) surfaced as a crash
    /// dialog. A collision simply loses the race — the user can re-invoke.
    /// </summary>
    private static async Task<ContentDialogResult> ShowDialogSafeAsync(ContentDialog dialog)
    {
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490))
        {
            return ContentDialogResult.None; // ElementNotFound: another dialog still closing
        }
        catch (InvalidOperationException)
        {
            return ContentDialogResult.None; // only-one-ContentDialog collision
        }
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
        var result = await ShowDialogSafeAsync(dialog);
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
                if (await ShowDialogSafeAsync(proceed) != ContentDialogResult.Primary)
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
        await ShowDialogSafeAsync(dialog);
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
                if (await ShowDialogSafeAsync(dialog) == ContentDialogResult.Primary)
                {
                    OnSettingsClick(this, new RoutedEventArgs());
                }
            });
        }

        ViewModel.Library.SaveSettings(settings with { LastUpdateCheckUtc = DateTimeOffset.UtcNow });
    }
}
