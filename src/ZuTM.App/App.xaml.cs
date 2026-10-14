// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using Microsoft.UI.Xaml;

namespace ZuTM.App;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Surface crashes in a dialog instead of dying silently; the log also
        // captures QEMU-side errors via QemuVmProcess.
        System.Diagnostics.Debug.WriteLine($"ZuTM unhandled: {e.Message}");
        LogUnhandled(e.Message, e.Exception);
        e.Handled = true;
    }

    /// <summary>
    /// Append-only trace in %LOCALAPPDATA%\ZuTM\crash.log. WinUI crashes the
    /// process with a stowed exception (0xC000027B) after XAML callbacks
    /// throw, and Windows Error Reporting keeps only a native dump there —
    /// without this file the managed stack (dialog name, bind path) is lost.
    /// </summary>
    internal static void LogUnhandled(string message, Exception? exception)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZuTM");
            Directory.CreateDirectory(dir);
            var line = new System.Text.StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:O}] unhandled: {message}")
                .AppendLine(exception?.ToString() ?? "<no managed exception object>");
            File.AppendAllText(Path.Combine(dir, "crash.log"), line.ToString());
        }
        catch (IOException)
        {
            // Logging must never become the crash.
        }
    }
}
