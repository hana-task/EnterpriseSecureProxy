using System.Net;

namespace SecureProxy.Tests.Forwarding;

/// <summary>
/// A fake destination: records every request it receives and answers with the given function.
/// The body is read at the moment of receiving, because the forwarder disposes the request afterwards.
/// </summary>
public sealed class RecordingHandler : HttpMessageHandler
{
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

    public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public RecordingHandler(HttpStatusCode statusCode, string body = "")
        : this(Answer(statusCode, body))
    {
    }

    public List<RecordedRequest> Requests { get; } = new();

    /// <summary>
    /// Forgets all recorded requests and sets how to answer from now on. The integration tests share one
    /// instance (the app keeps the handler it was given), so each test calls this before it starts.
    /// </summary>
    public void Reset(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        Requests.Clear();
        _respond = respond;
    }

    public void Reset(HttpStatusCode statusCode, string body = "") => Reset(Answer(statusCode, body));

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Answer(
        HttpStatusCode statusCode, string body) =>
        (_, _) => Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body) });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri!, body,
            request.Content?.Headers.ContentType?.MediaType,
            request.Headers.ToDictionary(header => header.Key, header => string.Join(", ", header.Value))));

        return await _respond(request, cancellationToken);
    }
}

public sealed record RecordedRequest(
    string Method, Uri Url, string? Body, string? ContentType, IReadOnlyDictionary<string, string> Headers);

/// <summary>Hands out clients that talk to the given handler instead of the network.</summary>
internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public FakeHttpClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name) =>
        new(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
}

/// <summary>A body whose length is not known in advance, like a chunked response.</summary>
internal sealed class UnknownLengthContent : HttpContent
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
