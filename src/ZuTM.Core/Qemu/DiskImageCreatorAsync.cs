// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Diagnostics;

namespace ZuTM.Core.Qemu;

/// <summary>
/// Async half of <see cref="DiskImageCreator"/>: creation runs while the UI
/// stays responsive, and cancellation kills qemu-img and removes the partial
/// image so a cancelled create never leaves a file that looks finished.
/// (qemu-img emits no progress for create — the caller shows an indeterminate
/// ring and states that the operation cannot be streamed.)
/// </summary>
public sealed partial class DiskImageCreator
{
    public async Task CreateAsync(DiskImageSpec spec, CancellationToken cancellationToken = default)
    {
        // ArgumentList, never an interpolated command line: paths with quotes
        // or spaces cannot inject additional qemu-img arguments.
        var startInfo = new ProcessStartInfo(_qemuImgPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in BuildCreateArguments(spec))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("qemu-img could not be started.");
        }

        using var killOnCancel = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process already gone — nothing to kill.
            }
        });

        string stderr;
        try
        {
            // Read stderr concurrently so a chatty qemu-img cannot fill the
            // pipe buffer and deadlock the exit wait.
            var readStderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            stderr = await readStderr.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.WaitForExit(5_000);
            TryDelete(spec.Path);
            throw;
        }

        if (process.ExitCode != 0)
        {
            TryDelete(spec.Path);
            throw new InvalidOperationException($"qemu-img failed: {stderr}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort — never mask the original failure.
        }
    }
}
