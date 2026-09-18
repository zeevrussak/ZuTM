// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
// SPICE display integration. ZuTM embeds the SPICE server in QEMU (see
// QemuCommandLineBuilder) and hosts remote-viewer (virt-viewer) as the
// console client — the same client tooling UTM builds against.

using System.Diagnostics;

namespace ZuTM.Spice;

/// <summary>Options for opening a SPICE console window.</summary>
public sealed record SpiceConsoleOptions
{
    /// <summary>Run fullscreen with no toolbar (like UTM's captured display).</summary>
    public bool FullScreen { get; init; }

    /// <summary>Disable SPICE image compression effects for lower latency on localhost.</summary>
    public bool DisableEffects { get; init; } = true;

    /// <summary>Enable SPICE smartcard redirection channel (rarely used).</summary>
    public bool EnableSmartcard { get; init; }

    /// <summary>Extra raw arguments appended verbatim.</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
}

/// <summary>Builds remote-viewer command lines; pure and unit-testable.</summary>
public static class RemoteViewerArguments
{
    /// <summary>remote-viewer argv for the given SPICE endpoint and options.</summary>
    public static IReadOnlyList<string> Build(string host, int port, SpiceConsoleOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        options ??= new SpiceConsoleOptions();
        List<string> arguments = [$"spice://{host}:{port}"];

        if (options.FullScreen)
        {
            arguments.Add("--kiosk");
        }

        if (options.DisableEffects)
        {
            arguments.Add("--spice-disable-effects=all");
        }

        if (options.EnableSmartcard)
        {
            arguments.Add("--spice-smartcard");
        }

        arguments.AddRange(options.ExtraArguments);
        return arguments;
    }
}

/// <summary>Locates and launches the bundled remote-viewer console client.</summary>
public sealed class SpiceConsoleLauncher
{
    /// <summary>Path of the remote-viewer executable, or null when not installed.</summary>
    public string? ExecutablePath { get; }

    public SpiceConsoleLauncher(string? executablePath = null)
    {
        ExecutablePath = executablePath ?? Discover();
    }

    /// <summary>Search order: explicit override, bundled runtime, then PATH.</summary>
    public static string? Discover()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "runtimes", "spice", "bin", "remote-viewer.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "runtimes", "spice", "bin", "remote-viewer.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "remote-viewer.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry — skip.
            }
        }

        return null;
    }

    /// <summary>Opens a console window for a running VM's SPICE server.</summary>
    public Process Start(string host, int port, SpiceConsoleOptions? options = null)
    {
        if (ExecutablePath is null)
        {
            throw new FileNotFoundException(
                "remote-viewer not found. Reinstall ZuTM (the SPICE runtime is part of the MSI) or install virt-viewer.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
        };
        foreach (var argument in RemoteViewerArguments.Build(host, port, options))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start remote-viewer.");
    }
}
