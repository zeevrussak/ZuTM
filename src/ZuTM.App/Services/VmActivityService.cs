// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.App.ViewModels;
using ZuTM.Core.Net;
using ZuTM.Core.Qemu;
using ZuTM.Core.Utm;

namespace ZuTM.App.Services;

/// <summary>
/// Everything a New-VM dialog promised, to run after it closes: fetch or
/// import the installer ISO, then create the blank disk. Steps run in order
/// (drives list order is the boot order), each surfaced as the VM's
/// <see cref="VmItemViewModel.Activity"/>. Failures keep the remaining steps
/// so the detail pane's Retry can resume where the plan stopped.
/// </summary>
public sealed record VmCreationPlan(
    string? IsoUrl,
    string? LocalIsoPath,
    string DriveInterface,
    DiskImageSpec? Disk);

/// <summary>
/// Owns every long-running VM operation. Work never runs behind a modal:
/// the dialog closes first, and the VM's list row, detail pane and the
/// status bar show live progress, pause/resume and cancel.
/// </summary>
public sealed class VmActivityService
{
    private readonly VmLibraryService _library;
    private readonly Func<Func<Task>, Task> _dispatcher;
    private readonly Dictionary<Guid, IsoDownload> _downloads = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _cancellations = [];
    private readonly Dictionary<Guid, (VmCreationPlan Plan, bool IsoDone)> _pendingPlans = [];

    public VmActivityService(VmLibraryService library, Func<Func<Task>, Task> dispatcher)
    {
        _library = library;
        _dispatcher = dispatcher;
    }

    private Task OnUiAsync(Func<Task> work) => _dispatcher(work);

    // -- Creation plan ---------------------------------------------------------

    /// <summary>Runs the dialog's deferred work on the freshly created VM.</summary>
    public Task RunCreationAsync(VmItemViewModel vm, VmCreationPlan plan) =>
        RunPlanAsync(vm, plan, isoDone: false);

    /// <summary>Resumes a creation plan that stopped mid-way (Retry in the detail pane).</summary>
    public Task RetryCreationAsync(VmItemViewModel vm)
    {
        if (!_pendingPlans.Remove(vm.Id, out var pending))
        {
            return Task.CompletedTask;
        }

        vm.Error = null;
        return RunPlanAsync(vm, pending.Plan, pending.IsoDone);
    }

    private async Task RunPlanAsync(VmItemViewModel vm, VmCreationPlan plan, bool isoDone)
    {
        if (!isoDone)
        {
            if (plan.IsoUrl is { } url)
            {
                if (!await RunDownloadStepAsync(vm, url, plan))
                {
                    return; // paused, cancelled, or failed — plan kept for retry when failed
                }
            }
            else if (plan.LocalIsoPath is { } localPath)
            {
                if (!await RunImportStepAsync(vm, localPath, plan))
                {
                    return;
                }
            }
        }

        if (plan.Disk is { } disk)
        {
            if (!await RunDiskStepAsync(vm, disk))
            {
                return;
            }
        }

        _pendingPlans.Remove(vm.Id);
    }

    private async Task<bool> RunDiskStepAsync(VmItemViewModel vm, DiskImageSpec disk)
    {
        var activity = new VmActivity(
            VmActivityKind.CreatingDisk,
            $"Creating {disk.SizeBytes / (1024.0 * 1024 * 1024):F0} GiB disk image",
            canPause: false,
            canCancel: true);
        var cancellation = new CancellationTokenSource();
        _cancellations[vm.Id] = cancellation;
        vm.Activity = activity;

        try
        {
            await _library.CreateDiskImageAsync(disk, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            await EndActivityAsync(vm);
            return false;
        }
        catch (Exception ex)
        {
            await FailAsync(vm, activity, "Disk creation failed", ex);
            return false;
        }

        await AttachDriveAsync(vm, new UtmDrive
        {
            ImageName = Path.GetFileName(disk.Path),
            ImageType = UtmValues.DriveImageType.Disk,
            Interface = UtmValues.DriveInterface.Virtio,
        });
        await EndActivityAsync(vm);
        return true;
    }

    private async Task<bool> RunImportStepAsync(VmItemViewModel vm, string localIsoPath, VmCreationPlan plan)
    {
        var activity = new VmActivity(
            VmActivityKind.ImportingIso,
            $"Importing {Path.GetFileName(localIsoPath)}",
            canPause: false,
            canCancel: true);
        var cancellation = new CancellationTokenSource();
        _cancellations[vm.Id] = cancellation;
        vm.Activity = activity;

        UtmDrive drive;
        try
        {
            var copied = await vm.Bundle.ImportDriveImageAsync(
                localIsoPath, isReadOnly: false, ReportProgress(activity), cancellation.Token);
            drive = copied with
            {
                ImageType = UtmValues.DriveImageType.Cd,
                Interface = plan.DriveInterface,
                IsReadOnly = true,
            };
        }
        catch (OperationCanceledException)
        {
            await EndActivityAsync(vm);
            return false;
        }
        catch (Exception ex)
        {
            await FailAsync(vm, activity, "ISO import failed", ex);
            return false;
        }

        await AttachDriveAsync(vm, drive);
        await EndActivityAsync(vm);
        return true;
    }

    // -- ISO download ----------------------------------------------------------

    private async Task<bool> RunDownloadStepAsync(VmItemViewModel vm, string url, VmCreationPlan plan)
    {
        var download = StartIsoDownload(vm, url, plan.DriveInterface);
        return await PumpDownloadAsync(vm, download, plan);
    }

    /// <summary>
    /// Starts (or finds) the in-flight download for this VM. The destination
    /// is the configured downloads folder when one is set — the ISO then
    /// stays there as an external drive reference — otherwise the bundle's
    /// own Data directory.
    /// </summary>
    private IsoDownload StartIsoDownload(VmItemViewModel vm, string url, string driveInterface)
    {
        if (_downloads.TryGetValue(vm.Id, out var existing))
        {
            return existing;
        }

        var configured = _library.Settings.DownloadsFolder;
        var external = !string.IsNullOrWhiteSpace(configured);
        var folder = external ? configured! : vm.Bundle.DataDirectory;
        Directory.CreateDirectory(folder);
        var download = new IsoDownload(
            url, Path.Combine(folder, RemoteImage.SuggestedFileName(url)), driveInterface, external);
        _downloads[vm.Id] = download;
        return download;
    }

    /// <summary>Runs a download to completion, pause, or failure; true only when the ISO landed.</summary>
    private async Task<bool> PumpDownloadAsync(VmItemViewModel vm, IsoDownload download, VmCreationPlan plan)
    {
        var activity = new VmActivity(
            VmActivityKind.DownloadingIso,
            $"Downloading {Path.GetFileName(download.DestinationPath)}",
            canPause: true,
            canCancel: true);
        vm.Activity = activity;

        DownloadStopReason outcome;
        try
        {
            outcome = await download.Downloader.DownloadAsync(
                download.Url,
                download.DestinationPath,
                ReportProgress(activity),
                download.Pause,
                download.Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // The engine already removed the staged bytes.
            _downloads.Remove(vm.Id);
            _pendingPlans.Remove(vm.Id);
            await EndActivityAsync(vm);
            return false;
        }
        catch (Exception ex)
        {
            _pendingPlans[vm.Id] = (plan, IsoDone: false);
            vm.Error = $"Download failed: {ex.Message}";
            activity.State = VmActivityState.Failed;
            activity.Failure = ex.Message;
            return false;
        }

        if (outcome == DownloadStopReason.Paused)
        {
            activity.State = VmActivityState.Paused;
            return false;
        }

        _downloads.Remove(vm.Id);
        await AttachIsoAsync(vm, download);
        await EndActivityAsync(vm);
        return true;
    }

    /// <summary>Resume a paused download (detail-pane Resume button).</summary>
    public Task ResumeDownloadAsync(VmItemViewModel vm) =>
        _downloads.TryGetValue(vm.Id, out var download)
            ? PumpDownloadAsync(vm, download, _pendingPlans.TryGetValue(vm.Id, out var pending) ? pending.Plan : PlanForDownloadOnly(download))
            : RetryCreationAsync(vm);

    private static VmCreationPlan PlanForDownloadOnly(IsoDownload download) =>
        new(download.Url, null, download.DriveInterface, Disk: null);

    /// <summary>Pause a running download without losing staged progress.</summary>
    public void PauseDownload(VmItemViewModel vm)
    {
        if (_downloads.TryGetValue(vm.Id, out var download))
        {
            download.PauseSource.IsPaused = true;
        }
    }

    /// <summary>Cancel the VM's current cancellable activity; downloads discard their staged bytes.</summary>
    public async Task CancelAsync(VmItemViewModel vm)
    {
        _pendingPlans.Remove(vm.Id);
        if (_downloads.Remove(vm.Id, out var download))
        {
            await download.Cancellation.CancelAsync();
            return; // PumpDownloadAsync observes the token and finishes the cleanup
        }

        if (_cancellations.Remove(vm.Id, out var cancellation))
        {
            await cancellation.CancelAsync();
        }
    }

    // -- Clone -------------------------------------------------------------------

    /// <summary>Clones a stopped VM with byte progress; a cancelled clone leaves no partial bundle.</summary>
    public async Task<VmItemViewModel?> CloneAsync(VmItemViewModel vm)
    {
        var activity = new VmActivity(
            VmActivityKind.Cloning,
            $"Cloning {vm.Name}",
            canPause: false,
            canCancel: true);
        var cancellation = new CancellationTokenSource();
        _cancellations[vm.Id] = cancellation;
        vm.Activity = activity;

        UtmBundle cloneBundle;
        try
        {
            cloneBundle = await vm.Bundle.CloneAsync(
                progress: ReportProgress(activity), cancellationToken: cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            await EndActivityAsync(vm);
            return null;
        }
        catch (Exception ex)
        {
            await FailAsync(vm, activity, "Clone failed", ex);
            return null;
        }

        var cloneVm = new VmItemViewModel(_library, cloneBundle);
        _library.VirtualMachines.Add(cloneVm);
        await EndActivityAsync(vm);
        return cloneVm;
    }

    // -- Shared plumbing -----------------------------------------------------------

    private IProgress<RemoteIsoDownloadProgress> ReportProgress(VmActivity activity) =>
        new Progress<RemoteIsoDownloadProgress>(p =>
        {
            activity.BytesReceived = p.BytesReceived;
            activity.TotalBytes = p.TotalBytes;
        });

    private async Task FailAsync(VmItemViewModel vm, VmActivity activity, string title, Exception ex)
    {
        _cancellations.Remove(vm.Id);
        await OnUiAsync(() =>
        {
            vm.Error = $"{title}: {ex.Message}";
            return Task.CompletedTask;
        });
        activity.State = VmActivityState.Failed;
        activity.Failure = ex.Message;
    }

    private async Task EndActivityAsync(VmItemViewModel vm)
    {
        _cancellations.Remove(vm.Id);
        await OnUiAsync(() =>
        {
            vm.Activity = null;
            return Task.CompletedTask;
        });
    }

    /// <summary>Attaches a drive to the bundle and persists it (drives order is the boot order).</summary>
    private async Task AttachDriveAsync(VmItemViewModel vm, UtmDrive drive)
    {
        await OnUiAsync(() =>
        {
            vm.Bundle.Configuration = vm.Bundle.Configuration with
            {
                Drives = [.. vm.Bundle.Configuration.Drives, drive],
            };
            vm.RaiseConfigurationChanged();
            return Task.CompletedTask;
        });
        await Task.Run(() => vm.Bundle.Save());
        // Saved — re-raise so detail rows show the final, on-disk truth.
        await OnUiAsync(() =>
        {
            vm.RaiseConfigurationChanged();
            return Task.CompletedTask;
        });
    }

    private async Task AttachIsoAsync(VmItemViewModel vm, IsoDownload download)
    {
        if (download.External)
        {
            var externalDrive = new UtmDrive
            {
                ImageName = null,
                ImageType = UtmValues.DriveImageType.Cd,
                Interface = download.DriveInterface,
                IsReadOnly = true,
            };
            await OnUiAsync(() =>
            {
                vm.Bundle.State = vm.Bundle.State with
                {
                    ExternalDrivePaths = new Dictionary<string, string>(vm.Bundle.State.ExternalDrivePaths)
                    {
                        [externalDrive.Identifier] = download.DestinationPath,
                    },
                };
                return Task.CompletedTask;
            });
            await AttachDriveAsync(vm, externalDrive);
            return;
        }

        await AttachDriveAsync(vm, new UtmDrive
        {
            ImageName = Path.GetFileName(download.DestinationPath),
            ImageType = UtmValues.DriveImageType.Cd,
            Interface = download.DriveInterface,
            IsReadOnly = true,
        });
    }

    /// <summary>One ISO download in flight (or paused) for a VM.</summary>
    private sealed class IsoDownload(
        string url,
        string destinationPath,
        string driveInterface,
        bool external)
    {
        public string Url { get; } = url;

        public string DestinationPath { get; } = destinationPath;

        public string DriveInterface { get; } = driveInterface;

        /// <summary>True when the ISO lives in the downloads folder (external reference), not the bundle.</summary>
        public bool External { get; } = external;

        public RemoteIsoDownloader Downloader { get; } = new();

        public PauseTokenSource PauseSource { get; } = new();

        public PauseToken Pause => PauseSource.Token;

        public CancellationTokenSource Cancellation { get; } = new();
    }
}
