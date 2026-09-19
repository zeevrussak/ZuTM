// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
// Cross-process record of running VMs so the GUI and `zutm` CLI can both
// control the same machines (GUI records, CLI reads; and vice versa).

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZuTM.Core.Qemu;

public sealed record VmRuntimeEntry
{
    [JsonPropertyName("uuid")]
    public required string Uuid { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("pid")]
    public int Pid { get; init; }

    [JsonPropertyName("qmpPort")]
    public int QmpPort { get; init; }

    [JsonPropertyName("spicePort")]
    public int SpicePort { get; init; }

    [JsonPropertyName("startedUtc")]
    public DateTimeOffset StartedUtc { get; init; }
}

/// <summary>
/// File-backed registry of running VMs (%APPDATA%\ZuTM\running.json), written
/// atomically. Prunes entries whose process no longer exists on load.
/// </summary>
public sealed class RuntimeRegistry
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, VmRuntimeEntry> _entries = new();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ZuTM", "running.json");

    public RuntimeRegistry(string? path = null)
    {
        _path = path ?? DefaultPath;
        Load();
    }

    public IReadOnlyList<VmRuntimeEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries.Values];
            }
        }
    }

    public VmRuntimeEntry? Find(Guid uuid) => Find(uuid.ToString("D"));

    public VmRuntimeEntry? Find(string idOrName)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(idOrName.ToLowerInvariant(), out var byKey))
            {
                return byKey;
            }

            return _entries.Values.FirstOrDefault(e =>
                string.Equals(e.Name, idOrName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void RecordStart(VmRuntimeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            _entries[entry.Uuid.ToLowerInvariant()] = entry;
            Save();
        }
    }

    public void RecordStop(string uuid)
    {
        lock (_lock)
        {
            if (_entries.Remove(uuid.ToLowerInvariant()))
            {
                Save();
            }
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, VmRuntimeEntry>>(File.ReadAllText(_path));
                if (loaded is not null)
                {
                    _entries = loaded;
                }
            }
        }
        catch (JsonException)
        {
            // Corrupt registry is recreated on next save.
        }

        // Drop stale entries: processes from a previous boot.
        var stale = _entries.Where(pair => !ProcessExists(pair.Value.Pid)).Select(pair => pair.Key).ToArray();
        if (stale.Length > 0)
        {
            foreach (var key in stale)
            {
                _entries.Remove(key);
            }

            Save();
        }
    }

    private static bool ProcessExists(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void Save()
    {
        // Called under _lock.
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var staging = _path + ".tmp";
        File.WriteAllText(staging, JsonSerializer.Serialize(_entries, SerializerOptions));
        if (File.Exists(_path))
        {
            File.Replace(staging, _path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(staging, _path);
        }
    }
}
