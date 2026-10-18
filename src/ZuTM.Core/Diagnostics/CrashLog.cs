// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — Apache License 2.0.

using System.Text;

namespace ZuTM.Core.Diagnostics;

/// <summary>
/// Append-only diagnostic trace for exceptions the app survives (and for the
/// last moments of ones it does not). WinUI's stowed-exception fail-fast
/// leaves Windows Error Reporting with a native-only dump, so without this
/// file the managed stack is lost.
/// <para>
/// Every write is best-effort and thread-safe: diagnostics must never become
/// the crash, so IO failures are swallowed. The file rotates when it exceeds
/// <see cref="_maxBytes"/> so a long-lived install cannot grow it forever.
/// </para>
/// </summary>
public sealed class CrashLog
{
    public const string DefaultFileName = "crash.log";
    public const string ArchiveFileName = "crash.prev.log";
    public const long DefaultMaxBytes = 512 * 1024;

    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly object _gate = new();

    /// <summary>Creates a log under <paramref name="directory"/> (default %LOCALAPPDATA%\ZuTM). No IO happens here.</summary>
    public CrashLog(string? directory = null, long maxBytes = DefaultMaxBytes)
    {
        _directory = directory ?? DefaultDirectory();
        _maxBytes = maxBytes;
    }

    public string FilePath => Path.Combine(_directory, DefaultFileName);

    public string ArchivePath => Path.Combine(_directory, ArchiveFileName);

    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZuTM");

    /// <summary>Appends a title plus (when given) the full exception details.</summary>
    public void Write(string title, Exception? exception) => Write(
        title + Environment.NewLine + (exception?.ToString() ?? "<no exception detail>"));

    /// <summary>Appends one timestamped entry; never throws.</summary>
    public void Write(string message)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > _maxBytes)
                {
                    File.Delete(ArchivePath);
                    File.Move(FilePath, ArchivePath);
                }

                var entry = new StringBuilder()
                    .Append('[').Append(DateTimeOffset.Now.ToString("O")).Append("] ")
                    .AppendLine(message)
                    .AppendLine();
                File.AppendAllText(FilePath, entry.ToString());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Swallowed on purpose: a broken log location must not take the
            // app (or the crash handling itself) down.
        }
    }
}
