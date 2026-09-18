// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
// Expect-style driver for a VM serial console exposed over TCP (the
// -serial tcp:… endpoints QemuCommandLineBuilder creates).

using System.Net.Sockets;
using System.Text;

namespace ZuTM.Core.Qemu;

/// <summary>
/// Interactive serial console session: accumulate output, wait for markers,
/// send lines. Used by the E2E suites and the test-environment installer to
/// drive guests (login, commands, poweroff) exactly like a human at the
/// console.
/// </summary>
public sealed class SerialConsoleSession : IAsyncDisposable
{
    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1); // NetworkStream writes are not thread-safe
    private readonly CancellationTokenSource _readerCts = new();
    private readonly Task _readerTask;
    private readonly StringBuilder _output = new();
    private readonly object _outputLock = new();
    private int _searchStart; // log offset after our most recent send
    private const int MaxLogChars = 2_000_000;

    /// <summary>Everything the console has emitted so far (bounded ring).</summary>
    public string Log
    {
        get
        {
            lock (_outputLock)
            {
                return _output.ToString();
            }
        }
    }

    private SerialConsoleSession(TcpClient tcpClient)
    {
        _tcpClient = tcpClient;
        _stream = tcpClient.GetStream()!;
        _readerTask = Task.Run(ReadLoopAsync);
    }

    /// <summary>Connects to a serial TCP endpoint (QEMU serial server).</summary>
    public static async Task<SerialConsoleSession> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var tcpClient = new TcpClient();
        try
        {
            await tcpClient.ConnectAsync(host, port, cancellationToken);
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }

        return new SerialConsoleSession(tcpClient);
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[8192];
        try
        {
            while (!_readerCts.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buffer, _readerCts.Token);
                if (read == 0)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, read);
                lock (_outputLock)
                {
                    _output.Append(text);
                    if (_output.Length > MaxLogChars)
                    {
                        var trimmed = _output.Length - MaxLogChars / 2;
                        _output.Remove(0, trimmed);
                        _searchStart = Math.Max(0, _searchStart - trimmed);
                    }
                }

                // Busybox ash's line editor queries the cursor position
                // (ESC[6n) and blocks until the terminal answers; a raw
                // session must reply "row 1, column 1" or the shell wedges.
                if (text.Contains("\x1b[6n", StringComparison.Ordinal))
                {
                    var reply = Encoding.UTF8.GetBytes("\x1b[1;1R");
                    await _writeLock.WaitAsync(_readerCts.Token);
                    try
                    {
                        await _stream.WriteAsync(reply, _readerCts.Token);
                        await _stream.FlushAsync(_readerCts.Token);
                    }
                    finally
                    {
                        _writeLock.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Console went away (VM stopped) — Log keeps what we captured.
        }
    }

    /// <summary>Waits until the pattern appears in console output. Returns the log up to and including the match.</summary>
    public async Task<string> WaitForAsync(string pattern, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        while (true)
        {
            string log;
            int searchFrom;
            lock (_outputLock)
            {
                log = _output.ToString();
                searchFrom = Math.Min(_searchStart, log.Length);
            }

            var index = log.IndexOf(pattern, searchFrom, StringComparison.Ordinal);
            if (index >= 0)
            {
                return log[..(index + pattern.Length)];
            }

            // Small poll; the reader loop does the heavy lifting.
            await Task.Delay(100, cts.Token);
        }
    }

    /// <summary>Sends a line (CR-terminated, as serial consoles expect). The sent text is recorded in <see cref="Log"/> for diagnostics.</summary>
    public async Task SendLineAsync(string line, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\r");
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }

        lock (_outputLock)
        {
            _output.Append("<<< ").AppendLine(line);
            // Wait searches must not match the marker inside our own echoed
            // command line — start looking after what we sent.
            _searchStart = _output.Length;
        }
    }

    /// <summary>
    /// Runs a command and waits for a completion marker. The sent line prints
    /// the marker as <c>echo MARK""ER</c> while the shell outputs <c>MARKER</c>,
    /// so the marker can never match the guest's echo of our own line —
    /// completion is proven by real output even when input echo is on.
    /// </summary>
    public async Task<string> RunAsync(string command, string marker, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        if (marker.Length < 2)
        {
            throw new ArgumentException("marker must be at least two characters", nameof(marker));
        }

        var split = marker.Insert(marker.Length / 2, "\"\"");
        await SendLineAsync($"{command}; echo {split}", cancellationToken);
        return await WaitForAsync(marker, timeout, cancellationToken);
    }

    /// <summary>Logs in at a getty prompt: waits for "login:", sends user, optionally password, then waits for a root shell prompt.</summary>
    public async Task LoginAsync(string user, string? password, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await WaitForAsync("login:", timeout, cancellationToken);
        await SendLineAsync(user, cancellationToken);
        if (password is not null)
        {
            // Wait for the password prompt before typing — getty must switch
            // to no-echo mode first, else the password leaks into the console.
            await WaitForAsync("Password:", timeout, cancellationToken);
            await SendLineAsync(password, cancellationToken);
        }

        await WaitForAsync("# ", timeout, cancellationToken);

        // Resync: wait for a marker that cannot match the echoed input line.
        const string marker = "ZUTM-LOGIN-SYNC";
        await SendLineAsync("echo ZUTM-LOGIN-\"\"SYNC", cancellationToken);
        await WaitForAsync(marker, timeout, cancellationToken);

        // Disable input echo: wait markers must appear only in command OUTPUT,
        // not in the guest's echo of the command line itself.
        await SendLineAsync("stty -echo", cancellationToken);
        await Task.Delay(200, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _readerCts.Cancel();
        try
        {
            await _readerTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
        }

        _tcpClient.Close();
        _readerCts.Dispose();
    }
}
