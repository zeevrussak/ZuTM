// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Net;
using System.Net.Http;

namespace ZuTM.Core.Tests.Net;

/// <summary>In-memory HTTP responses for exercising download/resolver code without touching the network.</summary>
public static class StubHttp
{
    /// <summary>An HttpClient that answers any request with the given payload (Content-Length included).</summary>
    public static HttpClient Serve(byte[] payload) => Respond(_ =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });

    /// <summary>An HttpClient that answers with a handler-produced response (handler runs on the caller's task).</summary>
    public static HttpClient Respond(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHttpMessageHandler(responder));

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}

/// <summary>A stream that yields one chunk then throws, simulating a connection dying mid-download.</summary>
public sealed class DyingStream(byte[] chunk) : MemoryStream(chunk)
{
    private bool _died;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_died)
        {
            throw new IOException("connection reset by stub");
        }

        _died = true;
        return await base.ReadAsync(buffer, cancellationToken);
    }
}
