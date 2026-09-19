// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZuTM.App.Services;

/// <summary>App-level settings persisted in %APPDATA%\ZuTM\settings.json.</summary>
public sealed record AppSettings
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Folder that holds .utm bundles (mirrors UTM's container directory).</summary>
    [JsonPropertyName("vmFolder")]
    public string VmFolder { get; init; } = DefaultVmFolder();

    /// <summary>Repository checked for online updates.</summary>
    [JsonPropertyName("updateRepository")]
    public string UpdateRepository { get; init; } = "zeevrussak/ZuTM";

    /// <summary>Automatically check for updates at startup (weekly cadence).</summary>
    [JsonPropertyName("checkForUpdates")]
    public bool CheckForUpdates { get; init; } = true;

    [JsonPropertyName("lastUpdateCheckUtc")]
    public DateTimeOffset? LastUpdateCheckUtc { get; init; }

    private static string DefaultVmFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documents, "ZuTM");
    }

    private static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZuTM");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), SerializerOptions);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch (JsonException)
        {
            // Corrupt settings fall back to defaults; recreated on next save.
        }

        return new AppSettings();
    }

    public void Save()
    {
        // Atomic: stage + replace so a crash mid-write never corrupts settings.
        Directory.CreateDirectory(SettingsDirectory);
        var staging = SettingsPath + ".tmp";
        File.WriteAllText(staging, JsonSerializer.Serialize(this, SerializerOptions));
        if (File.Exists(SettingsPath))
        {
            File.Replace(staging, SettingsPath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(staging, SettingsPath);
        }
    }
}
