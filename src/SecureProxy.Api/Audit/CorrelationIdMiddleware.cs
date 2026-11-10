namespace SecureProxy.Api.Audit;

/// <summary>
/// Gives every request one id that ties together the caller's answer, the technical log and the audit log.
///
/// - The caller may send its own id in X-Correlation-ID (so it can match our records to its own).
///   It is only a search key and grants nothing, but it is still checked: short and safe characters only,
///   because it is written to the logs. Otherwise a new id is created.
/// - The id is stored in HttpContext.TraceIdentifier, the place ASP.NET already has for a request id,
///   so every component that reads TraceIdentifier (the error writer, the exception handler) uses it.
/// - It is returned to the caller in X-Correlation-ID on every response, also on the destination's answers.
/// - It is put in a logging scope, so every technical log line of this request carries it.
///
/// It is read from the caller only. It is not forwarded to the destination.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    public const int MaxLength = 64; // the CorrelationId column in RequestLogs

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].ToString();
        if (!IsValid(correlationId))
            correlationId = Guid.NewGuid().ToString("N");

        context.TraceIdentifier = correlationId;

        // Added at the moment the response starts, not now: the error writer clears the headers
        // (response.Clear) before it writes, and a header set here would be lost.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await _next(context);
        }
    }

    /// <summary>1–64 characters, only letters, digits, '-', '_' and '.'. One header value only.</summary>
    public static bool IsValid(string value) =>
        value.Length is > 0 and <= MaxLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
