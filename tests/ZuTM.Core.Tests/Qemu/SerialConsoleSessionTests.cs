// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Net;
using System.Net.Sockets;
using System.Text;
using ZuTM.Core.Qemu;
using Xunit;

namespace ZuTM.Core.Tests.Qemu;

/// <summary>Scripted fake serial console over TCP.</summary>
public sealed class FakeSerialServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private TcpClient? _client;

    public int Port { get; }

    private FakeSerialServer(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Script steps: "wait:<text>" pauses until the client sends the text; "send:<text>" writes a raw console line.</summary>
    public static async Task<FakeSerialServer> StartAsync(string[] script, CancellationToken cancellationToken = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var server = new FakeSerialServer(listener);
        _ = Task.Run(() => server.RunAsync(script, cancellationToken), cancellationToken);
        return server;
    }

    private async Task RunAsync(string[] script, CancellationToken cancellationToken)
    {
        using var connected = await _listener.AcceptTcpClientAsync(cancellationToken);
        _client = connected;
        var stream = connected.GetStream();
        var pending = "";
        var buffer = new byte[1024];

        foreach (var step in script)
        {
            if (step.StartsWith("send:", StringComparison.Ordinal))
            {
                var payload = Encoding.UTF8.GetBytes(step["send:".Length..] + "\r\n");
                await stream.WriteAsync(payload, cancellationToken);
            }
            else if (step.StartsWith("wait:", StringComparison.Ordinal))
            {
                var expected = step["wait:".Length..];
                while (!pending.Contains(expected, StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        return;
                    }

                    pending += Encoding.UTF8.GetString(buffer, 0, read);
                }
            }
        }

        // Keep the connection open until disposal so the session can read.
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        _client?.Close();
        return ValueTask.CompletedTask;
    }
}

public class SerialConsoleSessionTests
{
    [Fact]
    public async Task WaitForSeesConsoleOutput()
    {
        await using var server = await FakeSerialServer.StartAsync(["send:Welcome to ZuTM guest"]);
        await using var session = await SerialConsoleSession.ConnectAsync("127.0.0.1", server.Port);

        var seen = await session.WaitForAsync("ZuTM guest", TimeSpan.FromSeconds(5));

        Assert.Contains("Welcome to ZuTM guest", seen);
    }

    [Fact]
    public async Task SendLine_ReachesConsole()
    {
        await using var server = await FakeSerialServer.StartAsync(
        [
            "wait:whoami",
            "send:ACK-WHOAMI", // console only sees its own output, not our echo
        ]);
        await using var session = await SerialConsoleSession.ConnectAsync("127.0.0.1", server.Port);

        await session.SendLineAsync("whoami");
        await session.WaitForAsync("ACK-WHOAMI", TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_WaitsForCompletionMarker()
    {
        await using var server = await FakeSerialServer.StartAsync(
        [
            "wait:uname -a; echo ZD\"\"ONE",
            "send:Linux guest 6.12.0",
            "send:ZDONE",
        ]);
        await using var session = await SerialConsoleSession.ConnectAsync("127.0.0.1", server.Port);

        var output = await session.RunAsync("uname -a", "ZDONE", TimeSpan.FromSeconds(5));

        Assert.Contains("6.12.0", output);
        Assert.Contains("ZDONE", output);
    }

    [Fact]
    public async Task LoginAsync_SequencesUserAndPassword()
    {
        await using var server = await FakeSerialServer.StartAsync(
        [
            "send:alpine login:",
            "wait:root",
            "send:Password:",
            "wait:hunter2",
            "send:# ",
            "wait:echo ZUTM-LOGIN-\"\"SYNC",
            "send:ZUTM-LOGIN-SYNC",
        ]);
        await using var session = await SerialConsoleSession.ConnectAsync("127.0.0.1", server.Port);

        await session.LoginAsync("root", "hunter2", TimeSpan.FromSeconds(5));

        Assert.Contains("# ", session.Log);
    }

    [Fact]
    public async Task WaitFor_TimesOutCleanly()
    {
        await using var server = await FakeSerialServer.StartAsync(["send:nothing relevant"]);
        await using var session = await SerialConsoleSession.ConnectAsync("127.0.0.1", server.Port);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.WaitForAsync("never appears", TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task AnswersAnsiCursorPositionQueries()
    {
        // Busybox ash wedges until the terminal replies to ESC[6n.
        await using var server = await FakeSerialServer.StartAsync(
        [
            "send:prompt> [6n",
            "wait:[1;1R",
            "send:DSR-UNBLOCKED",
        ]);
        await using var session = await SerialConsoleSession.ConnectAsync("127.0.0.1", server.Port);

        await session.WaitForAsync("DSR-UNBLOCKED", TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Log_BoundedButKeepsRecentOutput()
    {
        await using var server = await FakeSerialServer.StartAsync(["send:EARLY", "send:" + new string('x', 2_500_000), "send:LATE-MARKER"]);
        await using var session = await SerialConsoleSession.ConnectAsync("127.0.0.1", server.Port);

        await session.WaitForAsync("LATE-MARKER", TimeSpan.FromSeconds(10));

        Assert.DoesNotContain("EARLY", session.Log);      // trimmed away
        Assert.Contains("LATE-MARKER", session.Log);      // recent tail kept
    }
}
