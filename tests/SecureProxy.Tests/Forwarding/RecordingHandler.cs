using System.Net;

namespace SecureProxy.Tests.Forwarding;

/// <summary>
/// A fake destination: records every request it receives and answers with the given function.
/// The body is read at the moment of receiving, because the forwarder disposes the request afterwards.
/// </summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

    public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public RecordingHandler(HttpStatusCode statusCode, string body = "")
        : this((_, _) => Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body) }))
    {
    }

    public List<RecordedRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri!, body,
            request.Content?.Headers.ContentType?.MediaType,
            request.Headers.ToDictionary(header => header.Key, header => string.Join(", ", header.Value))));

        return await _respond(request, cancellationToken);
    }
}

internal sealed record RecordedRequest(
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
