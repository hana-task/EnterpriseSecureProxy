using System.Diagnostics;
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
    private readonly ILogger<UpstreamForwarder> _logger;

    public UpstreamForwarder(IHttpClientFactory httpClientFactory, ILogger<UpstreamForwarder> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// The network handler of the "upstream" client (registered in Program):
    /// - redirects are not followed, so a destination cannot send the proxy to another host (SSRF);
    /// - no tracing headers (traceparent, tracestate, baggage) are added. .NET adds them by default, and
    ///   their value can come from the caller's own traceparent, so the caller would control a header
    ///   that is not on the operation's allowlist.
    /// </summary>
    public static SocketsHttpHandler CreatePrimaryHandler() => new()
    {
        AllowAutoRedirect = false,
        ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator()
    };

    public async Task<ForwardResult> SendAsync(
        UpstreamRequest request, DestinationPolicy destination, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        // Retry policy: one more attempt, only for GET and only after a connection failure.
        // GET does not change anything at the destination, so repeating it cannot duplicate an action.
        // POST/PUT/PATCH/DELETE are never repeated: the first attempt may have reached the destination
        // before the connection dropped. Timeouts and HTTP error statuses are not retried either.
        var maxAttempts = request.HttpMethod == "GET" ? 2 : 1;

        // One token that is cancelled by either the caller (disconnects) or the timeout.
        // The result keeps it, so reading the body later is limited by the same timeout.
        var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            // Inside the try, so the source is released even if the value is invalid.
            // (OperationCheck rejects such a destination before this point.)
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(destination.TimeoutSeconds));

            // Both attempts share the same timeout, so retrying never makes the caller wait longer.
            for (var attempt = 1; ; attempt++)
            {
                // A message can be sent only once, so each attempt builds its own.
                using var message = CreateMessage(request);
                try
                {
                    var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
                    return ForwardResult.Responded(response, timeoutSource);
                }
                catch (HttpRequestException exception) when (attempt < maxAttempts)
                {
                    // Connection failure: go around the loop once more.
                    _logger.LogWarning(exception, "Destination {DestinationName} could not be reached; retrying once",
                        destination.Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            timeoutSource.Dispose();

            // Both cases throw the same exception. Only the caller's own token tells them apart:
            // if it is cancelled the caller left, otherwise our timeout fired.
            return ForwardResult.Failed(cancellationToken.IsCancellationRequested
                ? ForwardOutcome.CallerCancelled
                : ForwardOutcome.Timeout);
        }
        catch (HttpRequestException exception)
        {
            // The audit log only says "UpstreamUnavailable"; the cause (DNS, refused, TLS...) is kept here.
            // The exception names the host and port, never parameter values or headers.
            _logger.LogWarning(exception, "Destination {DestinationName} could not be reached", destination.Name);
            timeoutSource.Dispose();
            return ForwardResult.Failed(ForwardOutcome.ConnectionFailed);
        }
        catch
        {
            timeoutSource.Dispose();
            throw;
        }
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
