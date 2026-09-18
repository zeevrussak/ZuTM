// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Diagnostics;
using System.Net.Sockets;

namespace ZuTM.Core.Qemu;

public enum VmRunState
{
    Stopped,
    Starting,
    Running,
    Stopping,
}

/// <summary>A running QEMU virtual machine: process handle, QMP control channel, logs.</summary>
public sealed class QemuVmProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter? _logWriter;

    public QemuLaunchPlan Plan { get; }

    public QmpClient? Qmp { get; private set; }

    public VmRunState State { get; private set; } = VmRunState.Starting;

    public int ExitCode => _process.HasExited ? _process.ExitCode : 0;

    /// <summary>Raised when the QEMU process exits for any reason.</summary>
    public event EventHandler<int>? Exited;

    private QemuVmProcess(Process process, QemuLaunchPlan plan, StreamWriter? logWriter)
    {
        _process = process;
        Plan = plan;
        _logWriter = logWriter;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            State = VmRunState.Stopped;
            _logWriter?.Flush();
            Exited?.Invoke(this, process.ExitCode);
        };
    }

    /// <summary>
    /// Starts a VM. On success the QMP channel is connected (QEMU listens with
    /// wait=off, so the connection is retried briefly while QEMU boots).
    /// </summary>
    public static async Task<QemuVmProcess> StartAsync(
        QemuLaunchPlan plan,
        string? logPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!File.Exists(plan.ExecutablePath))
        {
            throw new FileNotFoundException(
                $"QEMU executable not found at '{plan.ExecutablePath}'. Run scripts/fetch-qemu.ps1 or set ZUTM_QEMU_ROOT.",
                plan.ExecutablePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = plan.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };

        StreamWriter? logWriter = logPath is null ? null : new StreamWriter(logPath, append: true) { AutoFlush = true };
        process.OutputDataReceived += OnProcessOutput;
        process.ErrorDataReceived += OnProcessOutput;
        void OnProcessOutput(object _, DataReceivedEventArgs e)
        {
            if (e.Data is not null)
            {
                logWriter?.WriteLine(e.Data);
            }
        }

        if (!process.Start())
        {
            logWriter?.Dispose();
            throw new InvalidOperationException("Failed to start QEMU process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var vm = new QemuVmProcess(process, plan, logWriter)
        {
            State = VmRunState.Running,
        };

        try
        {
            vm.Qmp = await ConnectQmpWithRetryAsync(plan.Ports.QmpPort, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await vm.DisposeAsync();
            throw;
        }
        catch (Exception)
        {
            await vm.DisposeAsync();
            throw;
        }

        return vm;
    }

    private static async Task<QmpClient> ConnectQmpWithRetryAsync(int port, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                return await QmpClient.ConnectAsync("127.0.0.1", port, cancellationToken);
            }
            catch (SocketException)
            {
                await Task.Delay(250, cancellationToken);
            }
        }

        throw new TimeoutException($"QEMU did not open the QMP port {port} within 10 seconds.");
    }

    /// <summary>Graceful ACPI shutdown; escalates to hard termination after <paramref name="graceTimeout"/>.</summary>
    public async Task StopAsync(TimeSpan? graceTimeout = null, CancellationToken cancellationToken = default)
    {
        if (State is VmRunState.Stopped or VmRunState.Stopping)
        {
            return;
        }

        State = VmRunState.Stopping;
        graceTimeout ??= TimeSpan.FromSeconds(30);

        try
        {
            if (Qmp is { IsConnected: true })
            {
                await Qmp.PowerDownAsync(cancellationToken);
            }
        }
        catch (QmpException)
        {
            // Fall through to forceful termination below.
        }

        try
        {
            await WaitForExitAsync(_process, graceTimeout.Value);
            return;
        }
        catch (TimeoutException)
        {
        }

        ForceTerminate();
    }

    private void ForceTerminate()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    private static async Task WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var linkedCts = new CancellationTokenSource(timeout);
        try
        {
            while (!process.HasExited)
            {
                await Task.Delay(100, linkedCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("QEMU did not exit in time.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (State is not (VmRunState.Stopped or VmRunState.Stopping))
        {
            await StopAsync(TimeSpan.FromSeconds(5));
        }

        try
        {
            await WaitForExitAsync(_process, TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            ForceTerminate();
        }

        if (Qmp is not null)
        {
            await Qmp.DisposeAsync();
            Qmp = null;
        }

        _logWriter?.Dispose();
        _process.Dispose();
    }
}

/// <summary>Scopes a VM's lifetime: disposal (graceful stop) runs even if the caller throws.</summary>
public static class QemuVmProcessLeaseExtensions
{
    public static IAsyncDisposable Lease(this QemuVmProcess vm) => new VmLease(vm);

    private sealed class VmLease(QemuVmProcess vm) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (vm.State != VmRunState.Stopped)
            {
                await vm.DisposeAsync();
            }
        }
    }
}
