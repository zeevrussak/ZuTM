// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZuTM.App.Services;
using ZuTM.App.ViewModels;

namespace ZuTM.App;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly VmLibraryService _library;
    private readonly UpdateViewModel _updates;

    public SettingsDialog(VmLibraryService library, UpdateViewModel updates)
    {
        _library = library;
        _updates = updates;
        InitializeComponent();

        VmFolderBox.Text = library.Settings.VmFolder;
        DownloadsFolderBox.Text = library.Settings.DownloadsFolder;
        UpdateCheckBox.IsChecked = library.Settings.CheckForUpdates;
        UpdateStatusText.Text = _updates.StatusText;

        PrimaryButtonClick += OnPrimaryButtonClick;
        _updates.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UpdateViewModel.StatusText))
            {
                UpdateStatusText.Text = _updates.StatusText;
            }

            if (e.PropertyName is nameof(UpdateViewModel.IsChecking))
            {
                UpdateProgress.IsActive = _updates.IsChecking;
                UpdateProgress.Visibility = _updates.IsChecking ? Visibility.Visible : Visibility.Collapsed;
            }

            if (e.PropertyName is nameof(UpdateViewModel.Error))
            {
                UpdateInfoBar.Severity = InfoBarSeverity.Error;
                UpdateInfoBar.Message = _updates.Error;
                UpdateInfoBar.IsOpen = _updates.HasError;
            }
        };
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e) =>
        await _updates.CheckAsync();

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var folder = VmFolderBox.Text.Trim();
        if (folder.Length == 0 || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            args.Cancel = true;
            UpdateInfoBar.Severity = InfoBarSeverity.Error;
            UpdateInfoBar.Message = "Enter a valid folder path.";
            UpdateInfoBar.IsOpen = true;
            return;
        }

        var downloads = DownloadsFolderBox.Text.Trim();
        if (downloads.Length > 0 && downloads.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            args.Cancel = true;
            UpdateInfoBar.Severity = InfoBarSeverity.Error;
            UpdateInfoBar.Message = "Enter a valid downloads folder path (or leave it empty).";
            UpdateInfoBar.IsOpen = true;
            return;
        }

        _library.SaveSettings(_library.Settings with
        {
            VmFolder = folder,
            DownloadsFolder = downloads,
            CheckForUpdates = UpdateCheckBox.IsChecked == true,
        });
    }

    private async void OnBrowseDownloadsFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        if (App.MainWindow is { } window)
        {
            // Unpackaged WinUI 3 pickers must be bound to a window handle.
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        }

        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add("*"); // FolderPicker refuses to show anything without one
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            DownloadsFolderBox.Text = folder.Path;
        }
    }

    private void OnOpenDownloadsFolderClick(object sender, RoutedEventArgs e)
    {
        var path = DownloadsFolderBox.Text.Trim();
        if (path.Length == 0)
        {
            ShowDialogInfo("Downloads go inside each VM's bundle (Documents\\ZuTM\\<VM>.utm\\Data).",
                InfoBarSeverity.Informational);
            return;
        }

        if (!Directory.Exists(path))
        {
            ShowDialogInfo("That folder does not exist yet — it is created on first download.", InfoBarSeverity.Warning);
            return;
        }

        _ = OpenFolderAsync(path);
    }

    private async System.Threading.Tasks.Task OpenFolderAsync(string path)
    {
        // WinRT folder launch: the shell opens the path, no process API involved.
        if (!await Windows.System.Launcher.LaunchFolderPathAsync(path))
        {
            ShowDialogInfo("Windows could not open that folder.", InfoBarSeverity.Warning);
        }
    }

    private void ShowDialogInfo(string message, InfoBarSeverity severity)
    {
        UpdateInfoBar.Severity = severity;
        UpdateInfoBar.Message = message;
        UpdateInfoBar.IsOpen = true;
    }
}
