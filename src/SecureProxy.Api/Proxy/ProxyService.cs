using SecureProxy.Api.Errors;
using SecureProxy.Api.Forwarding;
using SecureProxy.Api.Validation;

namespace SecureProxy.Api.Proxy;

/// <summary>
/// Runs one proxy request from start to end: checks → send to the destination → write the answer.
/// It decides which answer the caller gets: the destination's own response, or a proxy error.
///
/// It works on the HttpContext directly because the proxy handles raw streams: the validator reads
/// the request body itself, and the destination's response is streamed straight to the caller.
/// </summary>
public sealed class ProxyService
{
    private readonly RequestValidator _validator;
    private readonly UpstreamForwarder _forwarder;

    public ProxyService(RequestValidator validator, UpstreamForwarder forwarder)
    {
        _validator = validator;
        _forwarder = forwarder;
    }

    public async Task ExecuteAsync(HttpContext context)
    {
        // The caller's own token. The forwarder and the response writer tell "the caller left" apart
        // from "the destination timed out" by this exact token, so no other token may be passed.
        var callerToken = context.RequestAborted;

        try
        {
            // 1. All checks. A rejected request never reaches the destination.
            var validation = await _validator.ValidateAsync(context.Request, callerToken);
            if (!validation.Passed)
            {
                await ProxyErrorWriter.WriteAsync(context.Response, ProxyErrorCatalog.ForRejection(validation.Rejection!));
                return;
            }

            // 2. Send. The result holds the destination's open response, so it is disposed at the end.
            var destination = validation.Operation!.Destination;
            using var result = await _forwarder.SendAsync(validation.Request!, destination, callerToken);

            // 3. If the destination answered, stream its answer to the caller (any status, also 4xx/5xx).
            var outcome = result.Outcome == ForwardOutcome.Responded
                ? await UpstreamResponseWriter.WriteAsync(context.Response, result, destination.MaxResponseBytes)
                : result.Outcome;

            // 4. Decide whether a proxy error must still be written.
            if (outcome is ForwardOutcome.Responded or ForwardOutcome.CallerCancelled)
                return; // answered already, or nobody is waiting

            if (context.Response.HasStarted)
                return; // part of the body was sent; the writer already aborted the connection

            await ProxyErrorWriter.WriteAsync(context.Response, ProxyErrorCatalog.ForForwardFailure(outcome));
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            // The caller left during the checks or while the error was written. Nobody to answer.
        }
    }
}
