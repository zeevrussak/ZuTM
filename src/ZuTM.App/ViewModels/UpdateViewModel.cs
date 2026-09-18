// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Reflection;
using ZuTM.App.Mvvm;
using ZuTM.Update;

namespace ZuTM.App.ViewModels;

/// <summary>Drives the Settings ▸ Updates experience and the startup check.</summary>
public sealed class UpdateViewModel : ObservableObject
{
    private readonly UpdateChecker _checker;
    private readonly UpdateInstaller _installer;
    private UpdateCheckResult? _result;
    private bool _isChecking;
    private long _downloadedBytes;
    private string? _error;
    private Func<string, Task> _installHandoff = _ => Task.CompletedTask;

    public UpdateViewModel(UpdateChecker checker, UpdateInstaller installer)
    {
        _checker = checker;
        _installer = installer;
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => !IsChecking);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => Result?.Asset is not null);
    }

    public static SemanticVersion CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null
                ? new SemanticVersion()
                : SemanticVersion.Parse($"{version.Major}.{version.Minor}.{version.Build}");
        }
    }

    public AsyncRelayCommand CheckCommand { get; }

    public AsyncRelayCommand DownloadCommand { get; }

    /// <summary>Called with the verified installer path; the app closes and runs msiexec.</summary>
    public Func<string, Task> InstallHandoff
    {
        get => _installHandoff;
        set => SetProperty(ref _installHandoff, value);
    }

    public UpdateCheckResult? Result
    {
        get => _result;
        private set
        {
            if (SetProperty(ref _result, value))
            {
                DownloadCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (SetProperty(ref _isChecking, value))
            {
                CheckCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public long DownloadedBytes
    {
        get => _downloadedBytes;
        private set => SetProperty(ref _downloadedBytes, value);
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => Error is not null;

    public string StatusText => Result switch
    {
        null => $"Current version: {CurrentVersion}",
        { UpdateAvailable: true } => $"ZuTM {Result.LatestVersion} is available.",
        _ => $"ZuTM is up to date ({CurrentVersion}).",
    };

    public async Task CheckAsync()
    {
        IsChecking = true;
        Error = null;
        try
        {
            Result = await _checker.CheckAsync(CurrentVersion);
            if (Result is null)
            {
                Error = "Could not reach GitHub. Check your connection and try again.";
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    private async Task DownloadAsync()
    {
        if (Result?.Asset is not { } asset)
        {
            return;
        }

        Error = null;
        DownloadedBytes = 0;
        var progress = new Progress<long>(bytes => DownloadedBytes = bytes);
        try
        {
            var digest = asset.Digest;
            if (string.IsNullOrEmpty(digest))
            {
                Error = "Release does not publish a SHA-256 digest; refusing to install unverifiable updates.";
                return;
            }

            var verifiedPath = await _installer.DownloadVerifiedAsync(asset, digest, progress);
            await InstallHandoff(verifiedPath);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}
