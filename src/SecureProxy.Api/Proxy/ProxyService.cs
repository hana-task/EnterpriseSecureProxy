using SecureProxy.Api.Audit;
using SecureProxy.Api.Data.Entities;
using SecureProxy.Api.Errors;
using SecureProxy.Api.Forwarding;
using SecureProxy.Api.Validation;

namespace SecureProxy.Api.Proxy;

/// <summary>
/// Runs one proxy request from start to end: checks → audit → send to the destination → write the answer → audit.
/// It decides which answer the caller gets: the destination's own response, or a proxy error.
///
/// It works on the HttpContext directly because the proxy handles raw streams: the validator reads
/// the request body itself, and the destination's response is streamed straight to the caller.
/// </summary>
public sealed class ProxyService
{
    private readonly RequestValidator _validator;
    private readonly UpstreamForwarder _forwarder;
    private readonly RequestAuditor _auditor;

    public ProxyService(RequestValidator validator, UpstreamForwarder forwarder, RequestAuditor auditor)
    {
        _validator = validator;
        _forwarder = forwarder;
        _auditor = auditor;
    }

    public async Task ExecuteAsync(HttpContext context)
    {
        // The caller's own token. The forwarder and the response writer tell "the caller left" apart
        // from "the destination timed out" by this exact token, so no other token may be passed.
        var callerToken = context.RequestAborted;
        _auditor.Begin(context.TraceIdentifier); // the correlation id (CorrelationIdMiddleware)

        try
        {
            // 1. All checks. A rejected request never reaches the destination.
            var validation = await _validator.ValidateAsync(context.Request, callerToken);
            _auditor.SetOperation(validation.OperationKey, validation.Operation);
            if (!validation.Passed)
            {
                var rejection = ProxyErrorCatalog.ForRejection(validation.Rejection!);
                _auditor.SetResult(RequestOutcome.Rejected, validation.Rejection!.Reason.ToString(), rejection.StatusCode);
                await ProxyErrorWriter.WriteAsync(context.Response, rejection);
                return;
            }

            // 2. The audit policy: no record, no forwarding.
            if (!await _auditor.TrySaveBeforeForwardAsync())
            {
                _auditor.SetResult(RequestOutcome.ProxyError, "AuditUnavailable", ProxyErrorCatalog.AuditUnavailable.StatusCode);
                await ProxyErrorWriter.WriteAsync(context.Response, ProxyErrorCatalog.AuditUnavailable);
                return;
            }

            // 3. Send. The result holds the destination's open response, so it is disposed at the end.
            var destination = validation.Operation!.Destination;
            using var result = await _forwarder.SendAsync(validation.Request!, destination, callerToken);
            _auditor.SetForwarded((int?)result.Response?.StatusCode);

            // 4. If the destination answered, stream its answer to the caller (any status, also 4xx/5xx).
            var outcome = result.Outcome == ForwardOutcome.Responded
                ? await UpstreamResponseWriter.WriteAsync(context.Response, result, destination.MaxResponseBytes)
                : result.Outcome;

            // 5. Decide whether a proxy error must still be written, and what the audit records.
            if (outcome == ForwardOutcome.Responded)
            {
                var status = context.Response.StatusCode; // the destination's status, passed on unchanged
                _auditor.SetResult(status < 400 ? RequestOutcome.Success : RequestOutcome.UpstreamError, null, status);
                return;
            }

            if (outcome == ForwardOutcome.CallerCancelled)
            {
                _auditor.SetResult(RequestOutcome.CallerCancelled, null, null); // nobody is waiting
                return;
            }

            if (context.Response.HasStarted)
            {
                // Part of the destination's body was sent; the writer already aborted the connection.
                _auditor.SetResult(AuditOutcomeOf(outcome), outcome.ToString(), context.Response.StatusCode);
                return;
            }

            var failure = ProxyErrorCatalog.ForForwardFailure(outcome);
            _auditor.SetResult(AuditOutcomeOf(outcome), outcome.ToString(), failure.StatusCode);
            await ProxyErrorWriter.WriteAsync(context.Response, failure);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            // The caller left during the checks or while the error was written. Nobody to answer.
            _auditor.SetResult(RequestOutcome.CallerCancelled, null, null);
        }
        catch (Exception)
        {
            // Unexpected. Recorded without the exception's message (it may contain data); the exception
            // handler writes the generic 500 and logs the full detail to the technical log only.
            _auditor.SetResult(RequestOutcome.ProxyError, "UnhandledException", ProxyErrorCatalog.InternalError.StatusCode);
            throw;
        }
        finally
        {
            await _auditor.CompleteAsync();
        }
    }

    /// <summary>The audit category of a failure to get a full answer from the destination.</summary>
    private static RequestOutcome AuditOutcomeOf(ForwardOutcome outcome) => outcome switch
    {
        ForwardOutcome.Timeout => RequestOutcome.UpstreamTimeout,
        ForwardOutcome.ConnectionFailed => RequestOutcome.UpstreamUnavailable,
        _ => RequestOutcome.UpstreamError // ResponseTooLarge: the destination answered, but too much
    };
}
