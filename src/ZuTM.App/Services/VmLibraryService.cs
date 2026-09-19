// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Collections.ObjectModel;
using ZuTM.App.Mvvm;
using ZuTM.Core.Qemu;
using ZuTM.Core.Utm;
using ZuTM.Spice;

namespace ZuTM.App.Services;

public enum VmStatus
{
    Stopped,
    Starting,
    Running,
    Pausing,
    Paused,
    Resuming,
    Stopping,
}

/// <summary>UI-facing wrapper around one .utm bundle and its running process.</summary>
public sealed class VmItemViewModel : ObservableObject, IDisposable
{
    private readonly VmLibraryService _owner;
    private VmStatus _status;
    private string? _error;

    internal VmItemViewModel(VmLibraryService owner, UtmBundle bundle)
    {
        _owner = owner;
        Bundle = bundle;
    }

    public UtmBundle Bundle { get; }

    public Guid Id => Bundle.Id;

    public string Name => Bundle.Configuration.Information.Name;

    public string Architecture => Bundle.Configuration.System.Architecture;

    public int MemoryMib => Bundle.Configuration.System.MemorySizeMib;

    public string StatusText => Status switch
    {
        VmStatus.Running => "Running",
        VmStatus.Starting => "Starting…",
        VmStatus.Pausing => "Pausing…",
        VmStatus.Paused => "Paused",
        VmStatus.Resuming => "Resuming…",
        VmStatus.Stopping => "Stopping…",
        _ => "Stopped",
    };

    public string? Error
    {
        get => _error;
        internal set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => Error is not null;

    public VmStatus Status
    {
        get => _status;
        internal set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(CanPause));
                OnPropertyChanged(nameof(CanResume));
                OnPropertyChanged(nameof(CanReset));
                OnPropertyChanged(nameof(CanClone));
                OnPropertyChanged(nameof(CanDelete));
            }
        }
    }

    public bool CanStart => Status is VmStatus.Stopped;
    public bool CanStop => Status is VmStatus.Running or VmStatus.Paused;
    public bool CanPause => Status is VmStatus.Running;
    public bool CanResume => Status is VmStatus.Paused;
    public bool CanReset => Status is VmStatus.Running or VmStatus.Paused;
    public bool CanClone => Status is VmStatus.Stopped;
    public bool CanDelete => Status is VmStatus.Stopped;

    internal async Task SetProcessAsync(QemuVmProcess process, QemuPortSet? ports = null)
    {
        Status = VmStatus.Running;
        Error = null;
        Process = process;
        process.Exited += (_, code) =>
        {
            Process = null;
            SerialPort = 0;
            Status = VmStatus.Stopped;
            _owner._runtimeRegistry.RecordStop(Bundle.Id.ToString("D"));
            if (code != 0)
            {
                _ = _owner.DispatchAsync(() =>
                {
                    Error = $"QEMU exited with code {code}. See the VM's debug log (QEMU ▸ Debug Log) for details.";
                });
            }
        };

        // QMP STOP/RESUME events keep the UI truthful even when the guest is
        // paused by other means (guest agent, QEMU monitor).
        if (process.Qmp is not null)
        {
            process.Qmp.EventReceived += (_, message) =>
            {
                var @event = message.Event;
                if (@event == "STOP")
                {
                    _ = _owner.DispatchAsync(() => Status = VmStatus.Paused);
                }
                else if (@event == "RESUME")
                {
                    _ = _owner.DispatchAsync(() => Status = VmStatus.Running);
                }
            };
        }

        Bundle.State = Bundle.State with { LastStartedUtc = DateTimeOffset.UtcNow };
        await _owner.RunUiSafeAsync(() => Bundle.Save());
    }

    internal void ClearProcess()
    {
        Process = null;
        Status = VmStatus.Stopped;
    }

    internal QemuVmProcess? Process { get; private set; }

    /// <summary>Serial TCP endpoint while running (0 = none) — powers the in-app terminal.</summary>
    public int SerialPort
    {
        get => _serialPort;
        internal set => SetProperty(ref _serialPort, value);
    }

    private int _serialPort;

    public void Dispose() => Process?.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

/// <summary>Scans the VM folder, starts/stops VMs, owns runtimes and launchers.</summary>
public sealed class VmLibraryService : IDisposable
{
    private readonly Func<Func<Task>, Task> _uiDispatcher;

    public AppSettings Settings { get; private set; }

    public QemuRuntime? QemuRuntime { get; }

    public SpiceConsoleLauncher SpiceLauncher { get; } = new();

    public ObservableCollection<VmItemViewModel> VirtualMachines { get; } = [];

    public string? LoadError { get; private set; }

    internal readonly RuntimeRegistry _runtimeRegistry = new();

    public VmLibraryService(Func<Func<Task>, Task>? uiDispatcher = null, QemuRuntime? qemuRuntime = null)
    {
        _uiDispatcher = uiDispatcher ?? (work => work());
        Settings = AppSettings.Load();
        QemuRuntime = qemuRuntime ?? global::ZuTM.Core.Qemu.QemuRuntime.Discover();
    }

    internal Task DispatchAsync(Action action) =>
        _uiDispatcher(() => Task.Run(action));

    internal Task RunUiSafeAsync(Action action) =>
        _uiDispatcher(() => Task.Run(action));

    /// <summary>(Re)loads every .utm bundle in the VM folder.</summary>
    public async Task ReloadAsync()
    {
        VirtualMachines.Clear();

        List<VmItemViewModel> items = [];
        var loadError = (string?)null;
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Settings.VmFolder);
            foreach (var path in UtmBundle.FindBundles(Settings.VmFolder))
            {
                try
                {
                    items.Add(new VmItemViewModel(this, UtmBundle.Load(path)));
                }
                catch (Exception ex) when (ex is UtmConfigurationException or FileNotFoundException or FormatException)
                {
                    loadError ??= $"{Path.GetFileName(path)}: {ex.Message}";
                }
            }
        });

        foreach (var item in items.OrderBy(i => i.Name, StringComparer.CurrentCulture))
        {
            VirtualMachines.Add(item);
        }

        LoadError = loadError;
    }

    /// <summary>Starts a VM: shared launcher (plan → QEMU → QMP → registry), opens the SPICE console, keeps a serial session for the in-app terminal.</summary>
    public async Task StartAsync(VmItemViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        if (!CanStartVm(vm, out var reason))
        {
            vm.Error = reason;
            return;
        }

        vm.Status = VmStatus.Starting;
        vm.Error = null;
        try
        {
            var launcher = new VmLauncher(QemuRuntime!, _runtimeRegistry);
            var result = await launcher.StartAsync(vm.Bundle);
            var process = result.Process;
            var ports = result.Ports;
            await vm.SetProcessAsync(process, ports);

            // In-app serial terminal endpoint for the first serial port, if any.
            if (ports.SerialPorts.TryGetValue(0, out var serialPort))
            {
                vm.SerialPort = serialPort;
            }

            if (vm.Bundle.Configuration.Displays.Count > 0)
            {
                SpiceLauncher.Start("127.0.0.1", ports.SpicePort, new SpiceConsoleOptions { FullScreen = false });
            }
        }
        catch (Exception ex)
        {
            vm.ClearProcess();
            vm.Error = ex.Message;
        }
    }

    /// <summary>Graceful stop: ACPI power-down with escalation to terminate.</summary>
    public async Task StopAsync(VmItemViewModel vm)
    {
        if (vm.Process is not { } process)
        {
            return;
        }

        vm.Status = VmStatus.Stopping;
        await process.StopAsync(TimeSpan.FromSeconds(30));
        vm.ClearProcess();
        vm.Status = VmStatus.Stopped;
    }

    /// <summary>Freezes the guest CPUs (QMP stop); the RESUME event confirms.</summary>
    public async Task PauseAsync(VmItemViewModel vm)
    {
        if (vm.Process?.Qmp is not { } qmp)
        {
            return;
        }

        vm.Status = VmStatus.Pausing;
        try
        {
            await qmp.PauseAsync();
        }
        catch (QmpException ex)
        {
            vm.Error = ex.Message;
            vm.Status = VmStatus.Running;
        }
    }

    /// <summary>Continues a paused guest (QMP cont); the RESUME event confirms.</summary>
    public async Task ResumeAsync(VmItemViewModel vm)
    {
        if (vm.Process?.Qmp is not { } qmp)
        {
            return;
        }

        vm.Status = VmStatus.Resuming;
        try
        {
            await qmp.ResumeAsync();
        }
        catch (QmpException ex)
        {
            vm.Error = ex.Message;
            vm.Status = VmStatus.Paused;
        }
    }

    /// <summary>Hard reset: guest reboots immediately (unsaved state is lost).</summary>
    public async Task ResetAsync(VmItemViewModel vm)
    {
        if (vm.Process?.Qmp is not { } qmp)
        {
            return;
        }

        try
        {
            await qmp.ResetAsync();
        }
        catch (QmpException ex)
        {
            vm.Error = ex.Message;
        }
    }

    /// <summary>Clones a stopped VM into a sibling bundle with a fresh UUID.</summary>
    public async Task<VmItemViewModel> CloneAsync(VmItemViewModel vm)
    {
        if (vm.Status != VmStatus.Stopped)
        {
            throw new InvalidOperationException("Stop the VM before cloning it.");
        }

        var clone = await Task.Run(() => vm.Bundle.Clone());
        var cloneVm = new VmItemViewModel(this, clone);
        VirtualMachines.Add(cloneVm);
        return cloneVm;
    }

    /// <summary>Permanently deletes a stopped VM's bundle directory.</summary>
    public async Task DeleteAsync(VmItemViewModel vm)
    {
        if (vm.Status != VmStatus.Stopped)
        {
            throw new InvalidOperationException("Stop the VM before deleting it.");
        }

        await Task.Run(() => Directory.Delete(vm.Bundle.BundlePath, recursive: true));
        VirtualMachines.Remove(vm);
    }

    public bool CanStartVm(VmItemViewModel vm, out string? reason)
    {
        if (QemuRuntime is null)
        {
            reason = "QEMU runtime not found. Run scripts/fetch-qemu.ps1 or reinstall ZuTM.";
            return false;
        }

        if (vm.Status != VmStatus.Stopped)
        {
            reason = "The VM is not stopped.";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>Creates a new empty bundle on disk and returns its wrapper.</summary>
    public async Task<VmItemViewModel> CreateAsync(UtmConfiguration configuration)
    {
        Directory.CreateDirectory(Settings.VmFolder);
        var safeName = string.Join("_", configuration.Information.Name.Split(Path.GetInvalidFileNameChars()));
        var bundlePath = Path.Combine(Settings.VmFolder, safeName + ".utm");

        var index = 1;
        while (Directory.Exists(bundlePath))
        {
            bundlePath = Path.Combine(Settings.VmFolder, $"{safeName}-{index++}.utm");
        }

        var bundle = UtmBundle.CreateNew(bundlePath, configuration);
        await Task.Run(() => bundle.Save());

        var vm = new VmItemViewModel(this, bundle);
        VirtualMachines.Add(vm);
        return vm;
    }

    /// <summary>Creates a blank QCOW2 disk via qemu-img.</summary>
    public void CreateDiskImage(string path, long sizeMebiBytes)
    {
        var qemuImg = QemuRuntime is null ? null : Path.Combine(QemuRuntime.BinDirectory, "qemu-img.exe");
        if (qemuImg is null || !File.Exists(qemuImg))
        {
            throw new FileNotFoundException("qemu-img.exe not found in the QEMU runtime.");
        }

        // ArgumentList, never an interpolated command line: paths with quotes
        // or spaces cannot inject additional qemu-img arguments.
        var startInfo = new System.Diagnostics.ProcessStartInfo(qemuImg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("create");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("qcow2");
        startInfo.ArgumentList.Add(Path.GetFullPath(path));
        startInfo.ArgumentList.Add($"{sizeMebiBytes}M");

        var result = System.Diagnostics.Process.Start(startInfo);
        var stderr = result?.StandardError.ReadToEnd();
        result?.WaitForExit(30_000);
        if (result is null || result.ExitCode != 0)
        {
            throw new InvalidOperationException($"qemu-img failed: {stderr}");
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        settings.Save();
    }

    public void Dispose()
    {
        foreach (var vm in VirtualMachines)
        {
            vm.Dispose();
        }
    }
}
