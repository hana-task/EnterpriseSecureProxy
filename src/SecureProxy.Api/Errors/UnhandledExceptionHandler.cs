using Microsoft.AspNetCore.Diagnostics;

namespace SecureProxy.Api.Errors;

/// <summary>
/// The last line of defence: any exception nobody expected becomes a generic 500 in the usual
/// error format, with no exception message, stack trace or other internal detail.
///
/// Called by ASP.NET's exception handler middleware (UseExceptionHandler), which also:
/// - logs the exception itself, with the full detail, to the internal log only;
/// - skips this handler when the response has already started (the connection is then aborted);
/// - skips it when the caller cancelled the request.
///
/// One exception is the caller's fault, not ours: BadHttpRequestException, thrown by the server when it refuses
/// to read a request (for example a body over the admin API's [RequestSizeLimit]). It gets 413 or 400, not 500.
/// </summary>
public sealed class UnhandledExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var error = exception is BadHttpRequestException badRequest
            ? ProxyErrorCatalog.ForBadHttpRequest(badRequest.StatusCode)
            : ProxyErrorCatalog.InternalError;

        await ProxyErrorWriter.WriteAsync(context.Response, error);
        return true; // handled: no other handler runs and the exception is not rethrown
    }
}
