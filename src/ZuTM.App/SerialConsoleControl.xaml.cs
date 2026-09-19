// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
// In-app serial terminal: attach to the running VM's serial TCP endpoint and
// stream the console (the same SerialConsoleSession protocol the E2E suites
// drive unattended installs with).

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ZuTM.Core.Qemu;

namespace ZuTM.App;

public sealed partial class SerialConsoleControl : UserControl
{
    private SerialConsoleSession? _session;
    private DispatcherTimer? _timer;
    private int _shownCharacters;

    public SerialConsoleControl()
    {
        InitializeComponent();
        Unloaded += (_, _) => Detach();
    }

    /// <summary>Serial TCP endpoint to attach to (0 = none). Set by the host page.</summary>
    public int Endpoint { get; set; }

    private async void OnAttachClick(object sender, RoutedEventArgs e)
    {
        if (Endpoint <= 0)
        {
            StatusText.Text = "VM not running or has no serial port";
            return;
        }

        try
        {
            _session = await SerialConsoleSession.ConnectAsync("127.0.0.1", Endpoint);
            _shownCharacters = 0;
            ConnectButton.IsEnabled = false;
            DetachButton.IsEnabled = true;
            StatusText.Text = $"Attached to 127.0.0.1:{Endpoint}";

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => PumpOutput();
            _timer.Start();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Attach failed: {ex.Message}";
        }
    }

    private void OnDetachClick(object sender, RoutedEventArgs e) => Detach();

    private void Detach()
    {
        _timer?.Stop();
        _timer = null;
        if (_session is not null)
        {
            var session = _session;
            _ = session.DisposeAsync();
            _session = null;
        }

        ConnectButton.IsEnabled = true;
        DetachButton.IsEnabled = false;
        StatusText.Text = "Not attached";
    }

    private void PumpOutput()
    {
        if (_session is null)
        {
            return;
        }

        var log = _session.Log;
        if (log.Length > _shownCharacters)
        {
            // Cap the box to the last 64 KiB of console history.
            var chunk = log[_shownCharacters..];
            _shownCharacters = log.Length;
            Output.Text = (Output.Text + chunk)[^Math.Min(Output.Text.Length + chunk.Length, 64 * 1024)..];
            Output.Select(Output.Text.Length, 0);
        }
    }

    private async void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            await SendAsync();
        }
    }

    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        if (_session is null || Input.Text.Length == 0)
        {
            return;
        }

        var line = Input.Text;
        Input.Text = string.Empty;
        try
        {
            await _session.SendLineAsync(line);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Send failed: {ex.Message}";
        }
    }
}
