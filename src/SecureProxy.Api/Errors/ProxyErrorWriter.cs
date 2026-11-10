using Microsoft.AspNetCore.Mvc;
using SecureProxy.Api.Forwarding;

namespace SecureProxy.Api.Errors;

/// <summary>
/// Writes a ProxyError to the caller in one consistent format: ProblemDetails (RFC 7807,
/// application/problem+json) with the fixed errorCode and the correlationId, and the header
/// X-Proxy-Source: proxy, so the caller can tell it apart from an answer of the destination.
/// </summary>
public static class ProxyErrorWriter
{
    public const string ProxySourceValue = "proxy";

    /// <summary>Call only while the response has not started (HasStarted is false).</summary>
    public static Task WriteAsync(HttpResponse response, ProxyError error)
    {
        // Start from an empty response, so nothing set earlier (for example a destination header) leaks out.
        response.Clear();
        response.StatusCode = error.StatusCode;
        response.Headers[UpstreamResponseWriter.SourceHeaderName] = ProxySourceValue;

        var problem = new ProblemDetails
        {
            Status = error.StatusCode,
            Title = error.Title,
            Detail = error.Detail
        };
        problem.Extensions["errorCode"] = error.ErrorCode;
        problem.Extensions["correlationId"] = CorrelationId(response.HttpContext);

        return response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json",
            response.HttpContext.RequestAborted);
    }

    /// <summary>CorrelationIdMiddleware stores the correlation id in TraceIdentifier.</summary>
    private static string CorrelationId(HttpContext context) => context.TraceIdentifier;
}
