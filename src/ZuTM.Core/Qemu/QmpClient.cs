// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Buffers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ZuTM.Core.Qemu;

/// <summary>A single QMP response or asynchronous event.</summary>
public sealed record QmpMessage(JsonElement Raw)
{
    public string? Event => Raw.TryGetProperty("event", out var value) ? value.GetString() : null;

    public bool IsError => Raw.TryGetProperty("error", out _);

    public override string ToString() => Raw.GetRawText();
}

/// <summary>
/// QEMU Machine Protocol client over a TCP socket. A single reader task
/// parses the newline-delimited JSON stream and dispatches replies (by id)
/// to pending commands and events to <see cref="Events"/>/<see cref="EventReceived"/>.
/// </summary>
public sealed class QmpClient : IAsyncDisposable
{
    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly Queue<TaskCompletionSource<JsonElement>> _pendingIdLess = new();
    private readonly object _pendingLock = new();
    private readonly Channel<QmpMessage> _events = Channel.CreateUnbounded<QmpMessage>();
    private long _nextId;
    private Task _pumpTask = Task.CompletedTask;
    private string _leftover = "";

    /// <summary>Raised for every asynchronous event (STOP, RESUME, DEVICE_DELETED, …).</summary>
    public event EventHandler<QmpMessage>? EventReceived;

    /// <summary>Channel of all asynchronous events observed so far and in the future.</summary>
    public ChannelReader<QmpMessage> Events => _events.Reader;

    public bool IsConnected => _tcpClient.Connected;

    private QmpClient(TcpClient tcpClient)
    {
        _tcpClient = tcpClient;
        _stream = tcpClient.GetStream()!;
    }

    /// <summary>Connects to a QMP TCP endpoint and performs the qmp_capabilities handshake.</summary>
    public static async Task<QmpClient> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var tcpClient = new TcpClient();
        QmpClient client = null!;
        try
        {
            await tcpClient.ConnectAsync(host, port, cancellationToken);
            client = new QmpClient(tcpClient);

            var greeting = await client.ReadMessageAsync(cancellationToken);
            if (!greeting.TryGetProperty("QMP", out _))
            {
                throw new QmpException($"Expected QMP greeting, got: {greeting.GetRawText()}");
            }

            client.StartPump();
            await client.ExecuteAsync("qmp_capabilities", null, cancellationToken);
            return client;
        }
        catch
        {
            client?.DisposeInternal();
            tcpClient.Dispose();
            throw;
        }
    }

    private void StartPump() => _pumpTask = Task.Run(PumpAsync);

    private async Task PumpAsync()
    {
        try
        {
            while (!_pumpCts.IsCancellationRequested)
            {
                var message = await ReadMessageAsync(_pumpCts.Token);
                Dispatch(message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Socket closed — normal on VM shutdown; fail pending commands below.
        }

        FailAllPending(new QmpException("QMP connection closed."));
        _events.Writer.TryComplete();
    }

    private void Dispatch(JsonElement message)
    {
        if (message.TryGetProperty("event", out _))
        {
            var qmpMessage = new QmpMessage(message);
            EventReceived?.Invoke(this, qmpMessage);
            _events.Writer.TryWrite(qmpMessage);
            return;
        }

        TaskCompletionSource<JsonElement> waiter;
        lock (_pendingLock)
        {
            if (message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                && _pending.Remove(id.GetInt64(), out var byId))
            {
                waiter = byId;
            }
            else if (_pendingIdLess.Count > 0)
            {
                waiter = _pendingIdLess.Dequeue();
            }
            else
            {
                return; // stray reply with no waiter — drop
            }
        }

        waiter.TrySetResult(message.Clone());
    }

    private void FailAllPending(Exception error)
    {
        lock (_pendingLock)
        {
            foreach (var waiter in _pending.Values.Concat(_pendingIdLess))
            {
                waiter.TrySetException(error);
            }

            _pending.Clear();
            _pendingIdLess.Clear();
        }
    }

    /// <summary>Executes a QMP command and returns the "return" payload; throws <see cref="QmpException"/> on QMP errors.</summary>
    public async Task<JsonElement> ExecuteAsync(string command, object? arguments = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(!_tcpClient.Connected, this);

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
        {
            _pending[id] = completion;
        }

        try
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                var payload = new Dictionary<string, object?> { ["execute"] = command, ["id"] = id };
                if (arguments is not null)
                {
                    payload["arguments"] = arguments;
                }

                await WriteMessageAsync(payload, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _pumpCts.Token);
            using var registration = linked.Token.Register(() => completion.TrySetCanceled(linked.Token));
            // The pump may have terminated before this waiter was registered
            // (e.g. the server closed between commands); its completion fails
            // any such orphaned waiters instead of leaving them forever.
            // (Not disposed: the continuation tracks the pump's own lifetime.)
            _ = _pumpTask.ContinueWith(
                _ => completion.TrySetException(new QmpException("QMP connection closed.")),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            var reply = await completion.Task.WaitAsync(linked.Token);

            if (reply.TryGetProperty("error", out var error))
            {
                throw new QmpException($"QMP command '{command}' failed: {error.GetRawText()}");
            }

            return reply.TryGetProperty("return", out var value) ? value.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
        }
        finally
        {
            lock (_pendingLock)
            {
                _pending.Remove(id);
            }
        }
    }

    /// <summary>Queries the VM run state ("running", "paused", "shutdown", …).</summary>
    public async Task<string> QueryStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync("query-status", null, cancellationToken);
        return result.TryGetProperty("status", out var status) ? status.GetString() ?? "unknown" : "unknown";
    }

    /// <summary>Graceful ACPI shutdown request.</summary>
    public Task PowerDownAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("system_powerdown", null, cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("stop", null, cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("cont", null, cancellationToken);

    /// <summary>Hard reset of the VM.</summary>
    public Task ResetAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("system_reset", null, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            _pumpCts.Cancel();
            if (_tcpClient.Connected)
            {
                await _sendLock.WaitAsync(TimeSpan.FromSeconds(2));
                try
                {
                    await WriteMessageAsync(new Dictionary<string, object?> { ["execute"] = "quit" },
                        new CancellationToken(canceled: true));
                }
                catch
                {
                    // Best-effort goodbye; disposing the socket below is what matters.
                }
                finally
                {
                    _sendLock.Release();
                }
            }

            if (_pumpTask is not null)
            {
                try
                {
                    await _pumpTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                }
            }
        }
        finally
        {
            DisposeInternal();
        }
    }

    private void DisposeInternal()
    {
        _pumpCts.Cancel();
        _tcpClient.Close();
        FailAllPending(new ObjectDisposedException(nameof(QmpClient)));
    }

    private async Task<JsonElement> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var builder = new StringBuilder();
        try
        {
            while (true)
            {
                builder.Append(_leftover);
                _leftover = "";
                var text = builder.ToString();
                var newline = text.IndexOf('\n');
                if (newline >= 0)
                {
                    var line = text[..newline].Trim();
                    _leftover = text[(newline + 1)..];
                    return JsonDocument.Parse(line).RootElement.Clone();
                }

                var read = await _stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException("QMP server closed the connection.");
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task WriteMessageAsync(object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var framed = new byte[bytes.Length + 1];
        bytes.CopyTo(framed, 0);
        framed[^1] = (byte)'\n';
        await _stream.WriteAsync(framed, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }
}

/// <summary>QMP protocol or command failure.</summary>
public sealed class QmpException(string message) : Exception(message);

public static class QmpClientSnapshotExtensions
{
    /// <summary>Runs an HMP monitor command through QMP and returns its text output.</summary>
    public static async Task<string> HumanCommandAsync(this QmpClient client, string command, CancellationToken cancellationToken = default)
    {
        var result = await client.ExecuteAsync("human-monitor-command",
            new Dictionary<string, object?> { ["command-line"] = command },
            cancellationToken);
        return result.ValueKind == System.Text.Json.JsonValueKind.String
            ? result.GetString() ?? string.Empty
            : string.Empty;
    }

    /// <summary>Saves a full VM snapshot to the qcow2 disks + vmstate (HMP savevm).</summary>
    public static Task SaveSnapshotAsync(this QmpClient client, string name, CancellationToken cancellationToken = default) =>
        client.HumanCommandAsync($"savevm {name}", cancellationToken);

    /// <summary>Restores a snapshot (VM must be stopped; HMP loadvm).</summary>
    public static Task LoadSnapshotAsync(this QmpClient client, string name, CancellationToken cancellationToken = default) =>
        client.HumanCommandAsync($"loadvm {name}", cancellationToken);

    /// <summary>Deletes a snapshot from the disks (HMP delvm).</summary>
    public static Task DeleteSnapshotAsync(this QmpClient client, string name, CancellationToken cancellationToken = default) =>
        client.HumanCommandAsync($"delvm {name}", cancellationToken);

    /// <summary>Lists snapshot names (HMP "info snapshots" — parser shared with tests).</summary>
    public static async Task<IReadOnlyList<string>> ListSnapshotsAsync(this QmpClient client, CancellationToken cancellationToken = default)
    {
        var output = await client.HumanCommandAsync("info snapshots", cancellationToken);
        return ParseSnapshotList(output);
    }

    /// <summary>
    /// "info snapshots" output contains blocks like:
    ///   Snapshot list (from 00000000 to ...):
    ///   ...
    ///       ID        TAG                 VM SIZE                DATE       VM CLOCK
    ///       1         snap1                  4.2 MiB  2026-09-19 10:00:00   00:00:05.123
    /// Tag is the name savevm was given; it is the token without spaces.
    /// </summary>
    public static IReadOnlyList<string> ParseSnapshotList(string infoSnapshotsOutput)
    {
        List<string> names = [];
        foreach (var line in infoSnapshotsOutput.Split('\n'))
        {
            var trimmed = line.Trim('\r', ' ');
            if (trimmed.Length == 0 || trimmed.Trim('-').Length == 0
                || trimmed.StartsWith("ID", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Snapshot list", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Data rows may carry a device column ("--  1  tag …" or "h0 1 tag …"):
            // take the first all-numeric field; the following field is the tag.
            var fields = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var start = fields.Length > 0 && fields[0] == "--" ? 1 : 0;
            for (var i = start; i < fields.Length - 1; i++)
            {
                if (fields[i].Length > 0 && fields[i].All(char.IsDigit) && long.TryParse(fields[i], out _))
                {
                    var tag = fields[i + 1];
                    if (tag != "--" && !tag.Contains(':'))
                    {
                        names.Add(tag);
                    }

                    break;
                }
            }
        }

        return names;
    }
}
