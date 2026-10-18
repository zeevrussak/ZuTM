// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — Apache License 2.0.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZuTM.Core.Diagnostics;
using Windows.ApplicationModel.DataTransfer;

namespace ZuTM.App;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>A crash dialog is already up — don't stack another one on top.</summary>
    private static bool _crashDialogOpen;

    public App()
    {
        InitializeComponent();

        // XAML/UI-thread exceptions (WinUI re-raises these on the dispatcher).
        UnhandledException += OnUnhandledException;
        // Background-thread exceptions. The process is going down either way;
        // leave the last words where the user (and support) can read them.
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        // Fire-and-forget task faults. Not fatal since .NET Core, but each one
        // is a real bug flying blind if we don't record it.
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            MainWindow = new MainWindow();
            MainWindow.Activate();
        }
        catch (Exception ex)
        {
            // A launch failure leaves no window to disappear — say something.
            new CrashLog().Write("fatal: MainWindow construction failed", ex);
            NativeMessageBox(
                "ZuTM failed to start",
                "ZuTM could not start: " + ex.Message
                + "\n\nDetails were written to " + new CrashLog().FilePath);
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Keep the app alive so the user keeps their session, but make the
        // failure visible — a swallowed exception must never be a silent one.
        var log = new CrashLog();
        log.Write("XAML unhandled exception", e.Exception);
        e.Handled = true;
        ShowCrashDialog("Unhandled exception: " + e.Message, e.Exception, log);
    }

    private void OnAppDomainUnhandledException(object? sender, System.UnhandledExceptionEventArgs e)
    {
        new CrashLog().Write(
            e.IsTerminating ? "fatal exception (process terminating)" : "AppDomain exception",
            e.ExceptionObject as Exception);
        if (e.IsTerminating)
        {
            NativeMessageBox(
                "ZuTM has stopped working",
                "ZuTM hit an unrecoverable error and must close."
                + "\n\nDetails were written to " + new CrashLog().FilePath);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        new CrashLog().Write("unobserved task exception", e.Exception);
        e.SetObserved();
    }

    /// <summary>
    /// Shows what happened and what happens next, as a ContentDialog over the
    /// main window — or, when XAML itself is too broken, a native message box.
    /// Queued onto the dispatcher so the exception handler returns before any
    /// XAML work happens.
    /// </summary>
    internal static void ShowCrashDialog(string summary, Exception? exception, CrashLog? log = null)
    {
        log ??= new CrashLog();
        var details = summary + "\n\n" + (exception?.ToString() ?? "<no exception detail>");

        var queued = MainWindow?.DispatcherQueue?.TryEnqueue(() => ShowCrashDialogCore(summary, details, log)) ?? false;
        if (!queued)
        {
            // No dispatcher (pre-launch failure, torn-down window): native box.
            NativeMessageBox("ZuTM ran into a problem", details);
        }
    }

    private static void ShowCrashDialogCore(string summary, string details, CrashLog log)
    {
        if (_crashDialogOpen)
        {
            return; // one dialog at a time; the log has every occurrence
        }

        _crashDialogOpen = true;
        try
        {
            var window = MainWindow;
            if (window?.Content?.XamlRoot is not { } root)
            {
                NativeMessageBox("ZuTM ran into a problem", details);
                return;
            }

            var detailsBox = new TextBox
            {
                Text = details,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 200,
                IsSpellCheckEnabled = false,
            };
            var note = new TextBlock
            {
                Text = "Details were also written to " + log.FilePath,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
            };
            var dialog = new ContentDialog
            {
                Title = "ZuTM ran into a problem",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children = { detailsBox, note },
                },
                PrimaryButtonText = "Continue",
                SecondaryButtonText = "Copy details",
                CloseButtonText = "Close ZuTM",
                XamlRoot = root,
            };
            dialog.PrimaryButtonClick += (_, _) => _crashDialogOpen = false;
            dialog.CloseButtonClick += (_, _) =>
            {
                _crashDialogOpen = false;
                window.Close();
            };
            dialog.SecondaryButtonClick += (_, _) =>
            {
                try
                {
                    var package = new DataPackage();
                    package.SetText(details);
                    Clipboard.SetContent(package);
                }
                catch (Exception)
                {
                    // Clipboard can fail (locked by another process); the log
                    // file remains the durable copy either way.
                }
            };
            _ = dialog.ShowAsync();
        }
        catch (Exception)
        {
            _crashDialogOpen = false;
            NativeMessageBox("ZuTM ran into a problem", details);
        }
    }

    /// <summary>Win32 fallback for when XAML cannot show anything. Best-effort too.</summary>
    internal static void NativeMessageBox(string caption, string text)
    {
        try
        {
            _ = MessageBoxW(IntPtr.Zero, text, caption, 0);
        }
        catch (DllNotFoundException)
        {
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = false, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
