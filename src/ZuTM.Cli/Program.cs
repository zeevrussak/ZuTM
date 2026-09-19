// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
//
// zutm — command-line control of ZuTM virtual machines.
//
//   zutm list                          all bundles + running state
//   zutm show <name|uuid>              configuration summary
//   zutm start <name|uuid>             launch (detached) and record
//   zutm stop <name|uuid>              ACPI power-down via QMP (waits)
//   zutm pause | resume <name|uuid>    freeze / continue CPUs
//   zutm clone <name|uuid>             duplicate bundle with new identity
//   zutm delete <name|uuid>            remove bundle (must be stopped)
//
// VM folder resolution: --folder, then ZUTM_VM_FOLDER, then GUI settings.

using System.Text.Json;
using ZuTM.Core;
using ZuTM.Core.Qemu;
using ZuTM.Core.Utm;

namespace ZuTM.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        try
        {
            var command = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();
            var folder = ResolveFolder(ref rest);
            return command switch
            {
                "list" => List(folder),
                "show" => WithBundle(folder, rest, Show),
                "start" => await WithBundleAsync(folder, rest, StartAsync),
                "stop" => await WithBundleAsync(folder, rest, StopAsync),
                "pause" => await WithBundleAsync(folder, rest, (b, m) => LifecycleAsync(b, m, "pause")),
                "resume" => await WithBundleAsync(folder, rest, (b, m) => LifecycleAsync(b, m, "resume")),
                "clone" => WithBundle(folder, rest, Clone),
                "delete" => WithBundle(folder, rest, Delete),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            zutm — ZuTM virtual machines from the command line

            usage: zutm <command> [<name|uuid>] [--folder <path>]

            commands:
              list                       list VMs and their running state
              show    <vm>               print a configuration summary
              start   <vm>               start the VM (detached)
              stop    <vm>               ACPI power-down and wait for exit
              pause   <vm>               freeze the guest CPUs
              resume  <vm>               continue a paused guest
              clone   <vm>               duplicate the VM with a new identity
              delete  <vm>               permanently remove the VM bundle

            options:
              --folder <path>    VM folder (default: ZUTM_VM_FOLDER or GUI settings)

            ZuTM (c) 2026 Ze'ev Russak <zutm@20032014.xyz>
            """);
    }

    private static string ResolveFolder(ref string[] args)
    {
        string? folder = null;
        var filtered = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--folder" && i + 1 < args.Length)
            {
                folder = args[i + 1];
                i++;
            }
            else
            {
                filtered.Add(args[i]);
            }
        }

        args = [.. filtered];

        if (folder is not null)
        {
            return Path.GetFullPath(folder);
        }

        var env = Environment.GetEnvironmentVariable("ZUTM_VM_FOLDER");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(env);
        }

        // Same default as the GUI (Documents\ZuTM).
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ZuTM");
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}' — try 'zutm help'");
        return 2;
    }

    // -- commands -------------------------------------------------------------

    private static int List(string folder)
    {
        var registry = new RuntimeRegistry();
        var found = 0;
        foreach (var path in UtmBundle.FindBundles(folder))
        {
            UtmBundle bundle;
            try
            {
                bundle = UtmBundle.Load(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[unreadable] {Path.GetFileName(path)}: {ex.Message}");
                found++;
                continue;
            }

            var running = registry.Find(bundle.Id) is not null ? "running" : "stopped";
            Console.WriteLine($"{running,-9}  {bundle.Name,-24} {bundle.Id}  {bundle.Configuration.System.Architecture}");
            found++;
        }

        if (found == 0)
        {
            Console.WriteLine($"no VMs in {folder}");
        }

        return 0;
    }

    private static int Show(UtmBundle bundle)
    {
        var c = bundle.Configuration;
        Console.WriteLine($"name:        {c.Information.Name}");
        Console.WriteLine($"uuid:        {c.Information.Uuid:D}");
        Console.WriteLine($"backend:     {c.Backend} v{c.ConfigurationVersion}");
        Console.WriteLine($"system:      {c.System.Architecture} / {c.System.Target} / cpu {c.System.Cpu}, {c.System.CpuCount} vcpu, {c.System.MemorySizeMib} MiB");
        Console.WriteLine($"uefi:        {(c.Qemu.HasUefiBoot ? "yes" : "no")}   hypervisor: {(c.Qemu.HasHypervisor ? "yes" : "no")}");
        Console.WriteLine($"displays:    {(c.Displays.Count == 0 ? "headless" : string.Join(", ", c.Displays.Select(d => d.Hardware)))}");
        Console.WriteLine($"networks:    {(c.Networks.Count == 0 ? "none" : string.Join(", ", c.Networks.Select(n => $"{n.Mode}/{n.Hardware}")))}");
        Console.WriteLine($"drives:      {c.Drives.Count}");
        foreach (var drive in c.Drives)
        {
            Console.WriteLine($"             {drive.ImageType,-12} {drive.Interface,-8} {drive.ImageName ?? "(external)"}{(drive.IsReadOnly ? " [ro]" : "")}");
        }
        return 0;
    }

    private static async Task<int> StartAsync(UtmBundle bundle, RuntimeRegistry registry)
    {
        var runtime = QemuRuntime.Discover()
            ?? throw new InvalidOperationException("QEMU runtime not found — run scripts/fetch-qemu.ps1 or set ZUTM_QEMU_ROOT");
        var launcher = new VmLauncher(runtime, registry);
        var result = await launcher.StartAsync(bundle);
        Console.WriteLine($"started {bundle.Name}: qemu pid {result.Process.ProcessId}, qmp {result.Ports.QmpPort}, spice {result.Ports.SpicePort}");
        return 0;
    }

    private static async Task<int> StopAsync(UtmBundle bundle, RuntimeRegistry registry)
    {
        var entry = registry.Find(bundle.Id) ?? throw new InvalidOperationException("VM is not running");
        await using var qmp = await QmpClient.ConnectAsync("127.0.0.1", entry.QmpPort);
        await qmp.PowerDownAsync();
        Console.WriteLine($"power-down requested for {bundle.Name}");

        // Guests without acpid ignore ACPI; escalate to a hard kill like the GUI.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (!ProcessAlive(entry.Pid))
            {
                Console.WriteLine("stopped");
                return 0;
            }

            await Task.Delay(500);
        }

        if (ProcessAlive(entry.Pid))
        {
            System.Diagnostics.Process.GetProcessById(entry.Pid).Kill(entireProcessTree: true);
            Console.WriteLine("guest ignored ACPI power-down; terminated");
        }

        return 0;
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<int> LifecycleAsync(UtmBundle bundle, RuntimeRegistry registry, string action)
    {
        var entry = registry.Find(bundle.Id) ?? throw new InvalidOperationException("VM is not running");
        await using var qmp = await QmpClient.ConnectAsync("127.0.0.1", entry.QmpPort);
        if (action == "pause")
        {
            await qmp.PauseAsync();
        }
        else
        {
            await qmp.ResumeAsync();
        }

        Console.WriteLine($"{action} sent to {bundle.Name}");
        return 0;
    }

    private static int Clone(UtmBundle bundle)
    {
        var clone = bundle.Clone();
        Console.WriteLine($"cloned to {clone.BundlePath} ({clone.Name})");
        return 0;
    }

    private static int Delete(UtmBundle bundle)
    {
        if (new RuntimeRegistry().Find(bundle.Id) is not null)
        {
            throw new InvalidOperationException("stop the VM before deleting it");
        }

        Directory.Delete(bundle.BundlePath, recursive: true);
        Console.WriteLine($"deleted {bundle.BundlePath}");
        return 0;
    }

    // -- argument plumbing ----------------------------------------------------

    private static int WithBundle(string folder, string[] args, Func<UtmBundle, int> action)
    {
        var bundle = LoadBundle(folder, args);
        return action(bundle);
    }

    private static async Task<int> WithBundleAsync(string folder, string[] args, Func<UtmBundle, RuntimeRegistry, Task<int>> action)
    {
        var bundle = LoadBundle(folder, args);
        return await action(bundle, new RuntimeRegistry());
    }

    private static UtmBundle LoadBundle(string folder, string[] args)
    {
        if (args.Length != 1)
        {
            throw new ArgumentException("expected exactly one <name|uuid> argument");
        }

        var id = args[0];
        foreach (var path in UtmBundle.FindBundles(folder))
        {
            try
            {
                var bundle = UtmBundle.Load(path);
                if (bundle.Id.ToString("D").Equals(id, StringComparison.OrdinalIgnoreCase)
                    || bundle.Name.Equals(id, StringComparison.OrdinalIgnoreCase))
                {
                    return bundle;
                }
            }
            catch (UtmConfigurationException)
            {
                // skip unreadable bundles while searching
            }
        }

        throw new InvalidOperationException($"no VM named '{id}' in {folder}");
    }
}
