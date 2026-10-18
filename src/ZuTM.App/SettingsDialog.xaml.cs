// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — Apache License 2.0.

using System.IO;
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
        SelectTheme(library.Settings.Theme);
        VersionText.Text = $"ZuTM {typeof(App).Assembly.GetName().Version?.ToString(3) ?? "dev"}";
        RefreshDownloads();

        PrimaryButtonClick += OnPrimaryButtonClick;
        ThemeBox.SelectionChanged += (_, _) => ApplyThemePreview(SelectedTheme());
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

    private string SelectedTheme() => (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "system";

    private void SelectTheme(string theme)
    {
        for (var i = 0; i < ThemeBox.Items.Count; i++)
        {
            if ((ThemeBox.Items[i] as ComboBoxItem)?.Tag as string == theme)
            {
                ThemeBox.SelectedIndex = i;
                return;
            }
        }

        ThemeBox.SelectedIndex = 0;
    }

    /// <summary>Preview the theme live; the choice persists on Save.</summary>
    private static void ApplyThemePreview(string theme) => App.MainWindow?.ApplyTheme(theme);

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e) =>
        await _updates.CheckAsync();

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var folder = VmFolderBox.Text.Trim();
        if (folder.Length == 0 || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            args.Cancel = true;
            ShowDialogInfo("Enter a valid folder path.", InfoBarSeverity.Error);
            return;
        }

        var downloads = DownloadsFolderBox.Text.Trim();
        if (downloads.Length > 0 && downloads.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            args.Cancel = true;
            ShowDialogInfo("Enter a valid downloads folder path (or leave it empty).", InfoBarSeverity.Error);
            return;
        }

        _library.SaveSettings(_library.Settings with
        {
            VmFolder = folder,
            DownloadsFolder = downloads,
            CheckForUpdates = UpdateCheckBox.IsChecked == true,
            Theme = SelectedTheme(),
        });
    }

    private async void OnBrowseVmFolderClick(object sender, RoutedEventArgs e)
    {
        var picked = await PickFolderAsync();
        if (picked is not null)
        {
            VmFolderBox.Text = picked;
        }
    }

    private async void OnBrowseDownloadsFolderClick(object sender, RoutedEventArgs e)
    {
        var picked = await PickFolderAsync();
        if (picked is not null)
        {
            DownloadsFolderBox.Text = picked;
        }
    }

    private static async Task<string?> PickFolderAsync()
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
        return folder?.Path;
    }

    private void OnOpenVmFolderClick(object sender, RoutedEventArgs e) => OpenFolder(VmFolderBox.Text.Trim());

    private void OnOpenDownloadsFolderClick(object sender, RoutedEventArgs e)
    {
        var path = DownloadsFolderBox.Text.Trim();
        if (path.Length == 0)
        {
            ShowDialogInfo("Downloads go inside each VM's bundle (Documents\\ZuTM\\<VM>.utm\\Data).",
                InfoBarSeverity.Informational);
            return;
        }

        OpenFolder(path);
    }

    private void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            ShowDialogInfo("That folder does not exist yet — it is created on first use.", InfoBarSeverity.Warning);
            return;
        }

        _ = OpenFolderAsync(path);
    }

    private static async Task OpenFolderAsync(string path)
    {
        // WinRT folder launch: the shell opens the path, no process API involved.
        if (!await Windows.System.Launcher.LaunchFolderPathAsync(path))
        {
            App.MainWindow?.ShowError("Windows could not open that folder.");
        }
    }

    private void ShowDialogInfo(string message, InfoBarSeverity severity)
    {
        UpdateInfoBar.Severity = severity;
        UpdateInfoBar.Message = message;
        UpdateInfoBar.IsOpen = true;
    }

    // -- Downloads manager ------------------------------------------------------------

    private List<DownloadedIsoRow> _downloadRows = [];

    private void RefreshDownloads()
    {
        var folder = _library.Settings.DownloadsFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            DownloadsList.ItemsSource = null;
            DownloadsSummaryText.Text = string.IsNullOrWhiteSpace(folder)
                ? "No downloads folder is configured — ISOs download into each VM's bundle."
                : "The configured folder does not exist yet — it is created on the first download.";
            return;
        }

        var referenced = _library.VirtualMachines
            .SelectMany(vm => vm.Bundle.State.ExternalDrivePaths.Values)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _downloadRows = Directory.EnumerateFiles(folder, "*.iso")
            .Concat(Directory.EnumerateFiles(folder, "*.iso.part"))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var usedBy = _library.VirtualMachines.Count(vm =>
                    vm.Bundle.State.ExternalDrivePaths.Values.Contains(path, StringComparer.OrdinalIgnoreCase));
                var isPartial = path.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
                var sizeMib = isPartial ? 0 : new FileInfo(path).Length / (1024.0 * 1024.0);
                return new DownloadedIsoRow(
                    Path.GetFileName(path),
                    path,
                    isPartial
                        ? $"paused download — resume from the VM's detail pane"
                        : $"{sizeMib:F0} MiB · {(usedBy == 0 ? "not used by any VM" : $"used by {usedBy} VM(s)")}",
                    Deletable: usedBy == 0,
                    usedBy == 0
                        ? "Delete this file from disk"
                        : "Detach it from the VM(s) first — deleting a referenced ISO would break their CD drives");
            })
            .ToList();

        DownloadsList.ItemsSource = _downloadRows;
        var total = _downloadRows.Count;
        DownloadsSummaryText.Text = total == 0
            ? $"No ISOs in {folder} yet."
            : $"{total} file(s) in {folder}. Files referenced by a VM cannot be deleted here.";
    }

    private void OnDownloadsRefreshClick(object sender, RoutedEventArgs e) => RefreshDownloads();

    private void OnDownloadDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadedIsoRow { Deletable: true } row)
        {
            return;
        }

        try
        {
            File.Delete(row.Path);
        }
        catch (Exception ex)
        {
            ShowDialogInfo($"Delete failed: {ex.Message}", InfoBarSeverity.Error);
        }

        RefreshDownloads();
    }
}

/// <summary>One file in the downloads folder, with the VM count still referencing it.</summary>
public sealed record DownloadedIsoRow(
    string Name,
    string Path,
    string Detail,
    bool Deletable,
    string DeleteHint);
