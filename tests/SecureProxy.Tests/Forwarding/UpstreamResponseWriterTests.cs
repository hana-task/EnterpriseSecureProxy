using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using SecureProxy.Api.Forwarding;
using SecureProxy.Tests.Validation;

namespace SecureProxy.Tests.Forwarding;

public class UpstreamResponseWriterTests
{
    private static readonly Uri CustomerUrl = new("http://localhost:5101/customers/123");

    /// <summary>Sends through the real forwarder to a fake destination, so the result is a genuine ForwardResult.</summary>
    private static async Task<ForwardResult> ForwardAsync(HttpResponseMessage destinationResponse, int timeoutSeconds = 10)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(destinationResponse));
        var destination = TestData.CustomerGetPolicy().Destination with { TimeoutSeconds = timeoutSeconds };
        return await new UpstreamForwarder(new FakeHttpClientFactory(handler))
            .SendAsync(new UpstreamRequest("GET", CustomerUrl, null), destination, CancellationToken.None);
    }

    private static HttpResponseMessage ResponseWith(HttpStatusCode status, byte[] body) =>
        new(status) { Content = new ByteArrayContent(body) };

    private static (DefaultHttpContext Context, MemoryStream Body) NewCallerContext()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        return (context, body);
    }

    // ---- what reaches the caller ----

    [Fact]
    public async Task Status_and_body_of_the_destination_are_passed_on_and_marked_as_upstream()
    {
        var (context, body) = NewCallerContext();
        using var result = await ForwardAsync(ResponseWith(HttpStatusCode.NotFound, "not here"u8.ToArray()));

        var outcome = await UpstreamResponseWriter.WriteAsync(context.Response, result, maxResponseBytes: 1000);

        Assert.Equal(ForwardOutcome.Responded, outcome);
        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal("not here", System.Text.Encoding.UTF8.GetString(body.ToArray()));
        Assert.Equal("upstream", context.Response.Headers["X-Proxy-Source"]);
    }

    [Fact]
    public async Task Only_safe_response_headers_are_passed_on()
    {
        var upstream = ResponseWith(HttpStatusCode.OK, "{}"u8.ToArray());
        upstream.Content.Headers.ContentType = new("application/json");
        upstream.Headers.ETag = new("\"v1\"");
        upstream.Headers.TryAddWithoutValidation("Set-Cookie", "session=secret");
        upstream.Headers.TryAddWithoutValidation("Server", "internal-crm-7");
        upstream.Headers.TryAddWithoutValidation("X-Powered-By", "ASP.NET");
        upstream.Headers.Location = new Uri("http://10.0.0.5/internal");
        upstream.Headers.Connection.Add("keep-alive");
        var (context, _) = NewCallerContext();
        using var result = await ForwardAsync(upstream);

        await UpstreamResponseWriter.WriteAsync(context.Response, result, maxResponseBytes: 1000);

        var headers = context.Response.Headers;
        Assert.Equal("application/json", headers.ContentType.ToString());
        Assert.Equal("\"v1\"", headers.ETag.ToString());
        Assert.False(headers.ContainsKey("Set-Cookie"));
        Assert.False(headers.ContainsKey("Server"));
        Assert.False(headers.ContainsKey("X-Powered-By"));
        Assert.False(headers.ContainsKey("Location"));
        Assert.False(headers.ContainsKey("Connection"));
    }

    [Fact]
    public async Task Response_without_a_body_is_passed_on_with_its_status()
    {
        var (context, body) = NewCallerContext();
        using var result = await ForwardAsync(ResponseWith(HttpStatusCode.NoContent, Array.Empty<byte>()));

        var outcome = await UpstreamResponseWriter.WriteAsync(context.Response, result, maxResponseBytes: 1000);

        Assert.Equal(ForwardOutcome.Responded, outcome);
        Assert.Equal(204, context.Response.StatusCode);
        Assert.Empty(body.ToArray());
    }

    // ---- size limit ----

    [Fact]
    public async Task Announced_size_over_the_limit_is_refused_before_anything_is_sent()
    {
        var (context, body) = NewCallerContext();
        using var result = await ForwardAsync(ResponseWith(HttpStatusCode.OK, new byte[2000]));

        var outcome = await UpstreamResponseWriter.WriteAsync(context.Response, result, maxResponseBytes: 1000);

        Assert.Equal(ForwardOutcome.ResponseTooLarge, outcome);
        Assert.Empty(body.ToArray());
        Assert.False(context.Response.Headers.ContainsKey("X-Proxy-Source"));
    }

    [Fact]
    public async Task Body_that_grows_over_the_limit_without_an_announced_size_is_refused_before_anything_is_sent()
    {
        var (context, body) = NewCallerContext();
        using var result = await ForwardAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(new byte[2000]) });

        var outcome = await UpstreamResponseWriter.WriteAsync(context.Response, result, maxResponseBytes: 1000);

        Assert.Equal(ForwardOutcome.ResponseTooLarge, outcome);
        Assert.Empty(body.ToArray());
        Assert.False(context.Response.Headers.ContainsKey("X-Proxy-Source")); // untouched: the controller can still answer with an error
    }

    [Fact]
    public async Task Body_that_goes_over_the_limit_after_sending_has_started_aborts_the_connection()
    {
        var (context, body) = NewCallerContext();
        var abortRecorder = new AbortRecorder();
        context.Features.Set<IHttpRequestLifetimeFeature>(abortRecorder);
        context.Features.Set<IHttpResponseFeature>(new AlreadyStartedResponseFeature());
        using var result = await ForwardAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(new byte[30_000]) });

        var outcome = await UpstreamResponseWriter.WriteAsync(context.Response, result, maxResponseBytes: 10_000);

        Assert.Equal(ForwardOutcome.ResponseTooLarge, outcome);
        Assert.True(abortRecorder.WasAborted);
        Assert.True(body.Length <= 10_000, "no more than the limit may reach the caller");
    }

    // ---- timeout while reading the body (assignment test 13, second half) ----

    [Fact]
    public async Task Destination_that_stalls_while_sending_the_body_is_cut_off_by_the_timeout()
    {
        var (context, _) = NewCallerContext();
        using var result = await ForwardAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StallingContent() }, timeoutSeconds: 1);

        var outcome = await UpstreamResponseWriter.WriteAsync(context.Response, result, maxResponseBytes: 1000);

        Assert.Equal(ForwardOutcome.Timeout, outcome);
    }

    // ---- fakes ----

    /// <summary>A body whose length is not known in advance, like a chunked response.</summary>
    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _bytes;

        public UnknownLengthContent(byte[] bytes)
        {
            _bytes = bytes;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes, 0, _bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>A body whose bytes never arrive, like a destination that stalls after sending its headers.</summary>
    private sealed class StallingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new StallingStream());

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>A stream whose reads wait until they are cancelled, as a real network stream does.</summary>
    private sealed class StallingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class AbortRecorder : IHttpRequestLifetimeFeature
    {
        public bool WasAborted { get; private set; }
        public CancellationToken RequestAborted { get; set; }
        public void Abort() => WasAborted = true;
    }

    /// <summary>Pretends the response headers were already sent to the caller.</summary>
    private sealed class AlreadyStartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}
