using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Tests.Common.Network;

/// <summary>Every way a cloud call can go wrong that the Stage 12 failure matrix covers.</summary>
public enum NetworkFault
{
    None,
    /// <summary>The machine has no network at all (no route / network unreachable).</summary>
    NoNetwork,
    DnsFailure,
    ConnectionRefused,
    /// <summary>The TCP connection never completes.</summary>
    ConnectionTimeout,
    /// <summary>The connection is fine but the server never answers.</summary>
    RequestTimeout,
    Server500,
    ServerUnavailable503,
    /// <summary>HTTP 200 whose body is not what the protocol says.</summary>
    MalformedResponse,
    /// <summary>HTTP 200 with an empty body.</summary>
    EmptyResponse,
    Unauthorized401,
    Forbidden403,
    /// <summary>The server certificate is rejected (expired, untrusted, wrong host).</summary>
    TlsCertificateFailure,
    /// <summary>The connection drops while the response body is being read.</summary>
    ConnectionResetDuringBody
}

/// <summary>
/// A message handler that makes the next calls fail in a chosen, deterministic way, or passes them to the real inner handler
/// (a real in-process ASP.NET server, or nothing). Switching <see cref="Fault"/> back to None is "the cloud came back".
/// No real network, DNS or timing is involved except the timeouts, which are cancelled by a short client timeout.
/// </summary>
public sealed class FaultInjectingHandler : DelegatingHandler
{
    public FaultInjectingHandler(HttpMessageHandler? inner = null) : base(inner ?? new HttpClientHandler()) { }

    public NetworkFault Fault { get; set; }

    /// <summary>Number of requests that reached the handler, faulty or not.</summary>
    public int Requests { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        switch (Fault)
        {
            case NetworkFault.None:
                return await base.SendAsync(request, cancellationToken);
            case NetworkFault.NoNetwork:
                throw new HttpRequestException(HttpRequestError.ConnectionError, "Network is unreachable.", new SocketException((int)SocketError.NetworkUnreachable));
            case NetworkFault.DnsFailure:
                throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known.", new SocketException((int)SocketError.HostNotFound));
            case NetworkFault.ConnectionRefused:
                throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused.", new SocketException((int)SocketError.ConnectionRefused));
            case NetworkFault.ConnectionTimeout:
            case NetworkFault.RequestTimeout:
                await Task.Delay(Timeout.Infinite, cancellationToken);   // ends only when the client's own timeout cancels the call
                throw new InvalidOperationException("unreachable");
            case NetworkFault.Server500:
                return Respond(HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}");
            case NetworkFault.ServerUnavailable503:
                return Respond(HttpStatusCode.ServiceUnavailable, "<html>maintenance</html>", "text/html");
            case NetworkFault.MalformedResponse:
                return Respond(HttpStatusCode.OK, "{ this is not json <<<");
            case NetworkFault.EmptyResponse:
                return Respond(HttpStatusCode.OK, "");
            case NetworkFault.Unauthorized401:
                return Respond(HttpStatusCode.Unauthorized, "");
            case NetworkFault.Forbidden403:
                return Respond(HttpStatusCode.Forbidden, "");
            case NetworkFault.TlsCertificateFailure:
                throw new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established.",
                    new AuthenticationException("The remote certificate is invalid according to the validation procedure."));
            case NetworkFault.ConnectionResetDuringBody:
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenBodyContent() };
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private static HttpResponseMessage Respond(HttpStatusCode code, string body, string mediaType = "application/json")
        => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType) };

    /// <summary>A body that delivers a few bytes and then fails, like a dropped connection.</summary>
    private sealed class BrokenBodyContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new IOException("The connection was reset while reading the response.");

        protected override async Task<Stream> CreateContentReadStreamAsync()
            => await Task.FromResult<Stream>(new BrokenStream());

        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }

    private sealed class BrokenStream : Stream
    {
        private int _served;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Next(buffer.AsSpan(offset, count)));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Next(buffer.Span));
        private int Next(Span<byte> buffer)
        {
            if (_served > 0) throw new IOException("The connection was reset.");
            _served = Math.Min(4, buffer.Length);
            for (var i = 0; i < _served; i++) buffer[i] = 0x50;
            return _served;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
