// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — Apache License 2.0.

using ZuTM.App.Mvvm;

namespace ZuTM.App.ViewModels;

public enum VmActivityKind
{
    DownloadingIso,
    ImportingIso,
    CreatingDisk,
    Cloning,
    SavingSnapshot,
    RestoringSnapshot,
}

public enum VmActivityState
{
    Running,
    Paused,
    Failed,
}

/// <summary>
/// One long-running operation on a VM. Surfaced in three places at once —
/// the list row, the detail pane strip and the status bar — so the user can
/// always see (and pause or cancel) what a VM is busy with, without any
/// modal dialog in the way.
/// </summary>
public sealed class VmActivity : ObservableObject
{
    private const long Mib = 1024 * 1024;

    private VmActivityState _state;
    private long? _bytesReceived;
    private long? _totalBytes;
    private string? _failure;

    public VmActivity(VmActivityKind kind, string message, bool canPause, bool canCancel)
    {
        Kind = kind;
        Message = message;
        CanPause = canPause;
        CanCancel = canCancel;
    }

    public VmActivityKind Kind { get; }

    /// <summary>What is happening, e.g. "Downloading ubuntu-24.04.3-desktop-amd64.iso".</summary>
    public string Message { get; }

    /// <summary>False for operations the backend cannot interrupt mid-flight (snapshot save/restore).</summary>
    public bool CanPause { get; }

    public bool CanCancel { get; }

    public VmActivityState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(ShowPause));
                OnPropertyChanged(nameof(ShowResume));
                OnPropertyChanged(nameof(ShowCancel));
                OnPropertyChanged(nameof(ShowRetry));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    /// <summary>Affordance flags for the detail-pane strip.</summary>
    public bool ShowPause => CanPause && State == VmActivityState.Running;

    public bool ShowResume => IsPaused;

    public bool ShowCancel => CanCancel && State == VmActivityState.Running;

    public bool ShowRetry => IsFailed;

    public bool IsPaused => State == VmActivityState.Paused;

    public bool IsFailed => State == VmActivityState.Failed;

    /// <summary>Why the operation failed (Failed state) — also copied to the VM's error channel.</summary>
    public string? Failure
    {
        get => _failure;
        set
        {
            if (SetProperty(ref _failure, value))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public long? BytesReceived
    {
        get => _bytesReceived;
        set
        {
            if (SetProperty(ref _bytesReceived, value))
            {
                OnPropertyChanged(nameof(Percent));
                OnPropertyChanged(nameof(IsIndeterminate));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public long? TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (SetProperty(ref _totalBytes, value))
            {
                OnPropertyChanged(nameof(Percent));
                OnPropertyChanged(nameof(IsIndeterminate));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    /// <summary>0–100 when the transfer size is known; null means indeterminate.</summary>
    public double? Percent => BytesReceived is > 0 && TotalBytes is > 0
        ? BytesReceived * 100.0 / TotalBytes
        : null;

    public bool IsIndeterminate => Percent is null;

    /// <summary>One-line status for the row and the status bar, e.g. "Downloading ubuntu.iso — 43% · 612 of 1,400 MiB".</summary>
    public string Summary => State switch
    {
        VmActivityState.Failed => $"{Message} failed — {Failure}",
        VmActivityState.Paused => $"{Message} — paused, {TransferText()}",
        _ when TotalBytes is null && BytesReceived is null => Message,
        _ => $"{Message} — {TransferText()}",
    };

    private string TransferText()
    {
        return (BytesReceived, TotalBytes) switch
        {
            ({ } received, { } total) when total > 0 =>
                $"{received * 100.0 / total:F0}% · {received / (double)Mib:F0} of {total / (double)Mib:F0} MiB",
            ({ } received, _) => $"{received / (double)Mib:F0} MiB received",
            _ => "working…",
        };
    }
}
