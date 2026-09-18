// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZuTM.Core.Utm;

/// <summary>
/// ZuTM-private per-VM state, stored in <c>zutm-state.json</c> *inside* the
/// .utm bundle but strictly outside config.plist, so UTM never sees unknown
/// mutations of its own configuration. Deleting this file is harmless.
/// </summary>
public sealed record ZutmState
{
    public const int CurrentVersion = 1;
    public const string FileName = "zutm-state.json";

    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Absolute paths of external drive images keyed by drive Identifier (UTM stores macOS bookmarks instead; Windows paths are host-specific).</summary>
    [JsonPropertyName("externalDrivePaths")]
    public IReadOnlyDictionary<string, string> ExternalDrivePaths { get; init; } =
        new Dictionary<string, string>();

    /// <summary>UTC timestamp of the last time this VM was started by ZuTM.</summary>
    [JsonPropertyName("lastStartedUtc")]
    public DateTimeOffset? LastStartedUtc { get; init; }

    /// <summary>QEMU console window placement, persisted across runs.</summary>
    [JsonPropertyName("window")]
    public ZutmWindowState? Window { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ZutmState? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ZutmState>(File.ReadAllText(path), SerializerOptions);
        }
        catch (JsonException)
        {
            // State is advisory only; a corrupt file is recreated on next save.
            return null;
        }
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this with { Version = CurrentVersion }, SerializerOptions));
}

/// <summary>Console window placement (device-independent pixels, Windows coordinates).</summary>
public sealed record ZutmWindowState
{
    [JsonPropertyName("x")]
    public int X { get; init; }

    [JsonPropertyName("y")]
    public int Y { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("maximized")]
    public bool Maximized { get; init; }
}
