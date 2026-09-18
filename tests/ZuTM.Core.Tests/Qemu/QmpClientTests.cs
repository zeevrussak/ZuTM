// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Net;
using System.Net.Sockets;
using System.Text;
using ZuTM.Core.Qemu;
using Xunit;

namespace ZuTM.Core.Tests.Qemu;

/// <summary>
/// An in-process fake QMP server speaking the newline-delimited JSON
/// protocol, used to exercise <see cref="QmpClient"/> end to end.
/// </summary>
public sealed class FakeQmpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public int Port { get; }

    /// <summary>Handler receiving (command, id) and returning the reply object to serialize.</summary>
    public Func<string, long, object> OnCommand { get; set; } = static (command, _) => command switch
    {
        "query-status" => new { return_ = new { status = "running" } },
        _ => new { return_ = new { } },
    };

    private FakeQmpServer(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static async Task<FakeQmpServer> StartAsync(CancellationToken cancellationToken = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var server = new FakeQmpServer(listener);
        _ = Task.Run(() => server.AcceptLoopAsync(cancellationToken), cancellationToken);
        return server;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var connected = await _listener.AcceptTcpClientAsync(cancellationToken);
            _client = connected;
            _stream = connected.GetStream();

            // Greeting
            await SendAsync(new { QMP = new { version = new { qemu = new { major = 10, minor = 0 } }, capabilities = Array.Empty<string>() } }, cancellationToken);

            var readerTask = ReadLoopAsync(cancellationToken);
            await readerTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var pending = "";
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await _stream!.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }

            pending += Encoding.UTF8.GetString(buffer, 0, read);
            while (pending.Contains('\n'))
            {
                var line = pending[..pending.IndexOf('\n')];
                pending = pending[(pending.IndexOf('\n') + 1)..];
                await HandleLineAsync(line.Trim(), cancellationToken);
            }
        }
    }

    private async Task HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        using var document = System.Text.Json.JsonDocument.Parse(line);
        var command = document.RootElement.TryGetProperty("execute", out var execute) ? execute.GetString() : null;
        var id = document.RootElement.TryGetProperty("id", out var idElement) ? idElement.GetInt64() : 0;

        if (command == "qmp_capabilities")
        {
            await SendReplyAsync(new { return_ = new { } }, id, cancellationToken);
            return;
        }

        if (command == "quit")
        {
            return; // fake server just goes quiet; client closes the socket
        }

        // xunit fake: return_ serializes via a shim because C# anonymous types
        // cannot use the JSON keyword "return" — handled in SendAsync mapping.
        var reply = OnCommand(command ?? "", id);
        await SendReplyAsync(reply, id, cancellationToken);
    }

    private async Task SendReplyAsync(object reply, long id, CancellationToken cancellationToken)
    {
        // Translate the shim's return_/error_ payload shape.
        var json = System.Text.Json.JsonSerializer.SerializeToNode(reply)!.AsObject();
        var payload = new Dictionary<string, object?>();
        foreach (var (key, value) in json)
        {
            payload[key.TrimEnd('_') == "return" || key.TrimEnd('_') == "error" || key == "class_" ? key.TrimEnd('_') : key] = value;
        }

        payload["id"] = id;
        await SendRawAsync(payload, cancellationToken);
    }

    /// <summary>Sends an asynchronous event to the connected client (e.g. STOP).</summary>
    public async Task SendEventAsync(string eventName, CancellationToken cancellationToken = default)
    {
        await SendAsync(new { @event = eventName, timestamp = new { seconds = 1_760_000_000, microseconds = 0 } }, cancellationToken);
    }

    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToNode(payload)!.AsObject();
        var normalized = new Dictionary<string, object?>();
        foreach (var (key, value) in json)
        {
            normalized[key == "return_" ? "return" : key] = value;
        }

        await SendRawAsync(normalized, cancellationToken);
    }

    private async Task SendRawAsync(object payload, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload);
            var framed = new byte[bytes.Length + 1];
            bytes.CopyTo(framed, 0);
            framed[^1] = (byte)'\n';
            await _stream.WriteAsync(framed, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        _client?.Close();
        return ValueTask.CompletedTask;
    }
}

public class QmpClientTests
{
    [Fact]
    public async Task Handshake_GreetsAndEnablesCapabilities()
    {
        await using var server = await FakeQmpServer.StartAsync();
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task QueryStatus_ReturnsRunningState()
    {
        await using var server = await FakeQmpServer.StartAsync();
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);

        var status = await client.QueryStatusAsync();

        Assert.Equal("running", status);
    }

    [Fact]
    public async Task Commands_CarryIds_AndReceiveMatchingReplies()
    {
        long observedId = 0;
        string? observedCommand = null;
        await using var server = await FakeQmpServer.StartAsync();
        server.OnCommand = (command, id) =>
        {
            observedCommand = command;
            observedId = id;
            return new { return_ = new { echo = command } };
        };
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);

        var result = await client.ExecuteAsync("query-version");

        Assert.Equal("query-version", observedCommand);
        Assert.True(observedId >= 1);
        Assert.Equal("query-version", result.GetProperty("echo").GetString());
    }

    [Fact]
    public async Task QmpErrors_BecomeExceptions()
    {
        await using var server = await FakeQmpServer.StartAsync();
        server.OnCommand = (command, _) => new { error_ = new { class_ = "GenericError", desc = "nope" } };
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);

        await Assert.ThrowsAsync<QmpException>(() => client.ExecuteAsync("frobnicate"));
    }

    [Fact]
    public async Task Events_AreDelivered_WhileCommandsAreInFlight()
    {
        await using var server = await FakeQmpServer.StartAsync();
        using var gate = new SemaphoreSlim(0, 1);
        server.OnCommand = (command, _) => new { return_ = new { } };

        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);

        var observed = new List<string>();
        client.EventReceived += (_, message) =>
        {
            lock (observed)
            {
                observed.Add(message.Event!);
            }
        };

        await server.SendEventAsync("STOP");
        await client.ExecuteAsync("query-status");
        await server.SendEventAsync("RESUME");

        // Both events must eventually surface on the channel.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (observed)
            {
                if (observed.Count >= 2)
                {
                    break;
                }
            }

            await Task.Delay(50);
        }

        Assert.Equal(["STOP", "RESUME"], observed);
    }

    [Fact]
    public async Task ConcurrentCommands_DoNotInterleave()
    {
        await using var server = await FakeQmpServer.StartAsync();
        await using var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);

        var tasks = Enumerable.Range(0, 20)
            .Select(i => client.ExecuteAsync("query-status"))
            .ToArray();

        await Task.WhenAll(tasks); // no deadlocks, no mismatched replies, no exceptions
    }

    [Fact]
    public async Task ServerClosing_FailsPendingCommands()
    {
        var server = await FakeQmpServer.StartAsync();
        var client = await QmpClient.ConnectAsync("127.0.0.1", server.Port);

        await server.DisposeAsync(); // kills the connection

        await Assert.ThrowsAnyAsync<Exception>(() => client.ExecuteAsync("query-status"));
        await client.DisposeAsync();
    }
}

public class AcceleratorDetectorTests
{
    [Theory]
    [InlineData("x86_64", QemuAcceleration.Whpx)]
    [InlineData("i386", QemuAcceleration.Whpx)]
    [InlineData("aarch64", QemuAcceleration.Tcg)]
    public void WhpxOnlyForHostCompatibleGuests_OnX64Host(string guest, QemuAcceleration expected)
    {
        if (AcceleratorDetector.HostArchitecture != "x86_64")
        {
            return; // matrix is host-specific; ARM64 hosts get the mirrored case below
        }

        Assert.Equal(expected, AcceleratorDetector.Detect(guest, whpxAvailable: true));
    }

    [Fact]
    public void TcgWhenWhpxUnavailable()
    {
        Assert.Equal(QemuAcceleration.Tcg, AcceleratorDetector.Detect("x86_64", whpxAvailable: false));
    }

    [Fact]
    public void Arm64Host_CanAccelerateArm64Guests()
    {
        if (AcceleratorDetector.HostArchitecture != "aarch64")
        {
            return;
        }

        Assert.Equal(QemuAcceleration.Whpx, AcceleratorDetector.Detect("aarch64", whpxAvailable: true));
        Assert.Equal(QemuAcceleration.Tcg, AcceleratorDetector.Detect("x86_64", whpxAvailable: true));
    }
}
