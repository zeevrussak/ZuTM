// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — Apache License 2.0.

namespace ZuTM.Update;

/// <summary>How an install run ended, with the raw exit code for reporting.</summary>
public enum InstallerOutcome
{
    Success,
    SuccessRebootRequired,
    UserCancelled,
    Failed,
}

/// <summary>How an install run ended, with the raw exit code for reporting.</summary>
public readonly record struct UpdateInstallResult(InstallerOutcome Outcome, int ExitCode)
{
    /// <summary>The Windows Installer log (%TEMP%\MSI*.log when MsiLogging is on) for failures.</summary>
    public bool ShowLogHint => Outcome == InstallerOutcome.Failed;
}

public static class UpdateInstallRunner
{
    /// <summary>
    /// Runs the verified MSI (full UI, <see cref="UpdateInstaller.StartInstaller"/>) and
    /// waits for the outcome. Callers keep the app alive until this returns so a
    /// failed or cancelled install can be reported instead of the window simply
    /// disappearing.
    /// </summary>
    public static async Task<UpdateInstallResult> InstallAsync(
        string installerPath,
        CancellationToken cancellationToken = default)
    {
        using var process = UpdateInstaller.StartInstaller(installerPath, interactive: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        return new UpdateInstallResult(ClassifyExitCode(process.ExitCode), process.ExitCode);
    }

    /// <summary>Windows Installer exit codes ZuTM can act on; everything else is a failure.</summary>
    public static InstallerOutcome ClassifyExitCode(int exitCode) => exitCode switch
    {
        0 => InstallerOutcome.Success,
        3010 => InstallerOutcome.SuccessRebootRequired,
        1602 => InstallerOutcome.UserCancelled, // UAC declined or the wizard's Cancel
        _ => InstallerOutcome.Failed,
    };
}
