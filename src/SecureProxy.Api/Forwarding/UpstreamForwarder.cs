using System.Text;
using SecureProxy.Api.Policies;

namespace SecureProxy.Api.Forwarding;

/// <summary>
/// Sends a validated UpstreamRequest to the destination and reports how it ended.
///
/// - The HttpClient comes from IHttpClientFactory, which reuses connections and rotates them
///   (so DNS changes are picked up), instead of a new HttpClient per request.
/// - Only the response headers are awaited (ResponseHeadersRead); the body is streamed later.
/// </summary>
public sealed class UpstreamForwarder
{
    /// <summary>The named client registered in Program (no redirects, no client-level timeout).</summary>
    public const string HttpClientName = "upstream";

    private readonly IHttpClientFactory _httpClientFactory;

    public UpstreamForwarder(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<ForwardResult> SendAsync(
        UpstreamRequest request, DestinationPolicy destination, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var message = CreateMessage(request);

        var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return ForwardResult.Responded(response);
    }

    /// <summary>Everything here comes from the UpstreamRequest, which was built from configuration and validated input.</summary>
    private static HttpRequestMessage CreateMessage(UpstreamRequest request)
    {
        var message = new HttpRequestMessage(new HttpMethod(request.HttpMethod), request.Url);

        if (request.BodyJson is not null)
            message.Content = new StringContent(request.BodyJson, Encoding.UTF8, "application/json");

        foreach (var header in request.Headers)
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);

        return message;
    }
}
