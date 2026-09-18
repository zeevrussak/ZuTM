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

        _library.SaveSettings(_library.Settings with
        {
            VmFolder = folder,
            CheckForUpdates = UpdateCheckBox.IsChecked == true,
        });
    }
}
