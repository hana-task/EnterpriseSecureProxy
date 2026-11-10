using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Data;
using SecureProxy.Api.Data.Entities;
using SecureProxy.Api.Policies;
using Serilog;

namespace SecureProxy.Api.Audit;

/// <summary>
/// Keeps the audit record (one RequestLogs row) of the current request and applies the audit policy:
///
/// - Before forwarding, the row is saved with Outcome = InProgress. This save is mandatory: if it fails,
///   the request is not forwarded (fail closed). A request never reaches a destination without a trace.
/// - At the end, the same row is completed with the real outcome. Rejected requests get their only save here.
///   This save is best-effort: if SQL Server is unavailable, the caller still gets the normal answer and the
///   full record goes to the technical log instead, so it can be found by its correlation id.
///
/// Only fixed fields are stored: no body, headers, URL, parameter values, keys or messages from the caller.
/// Scoped: one instance, and one row, per request.
/// </summary>
public sealed class RequestAuditor
{
    private readonly ProxyDbContext _db;
    private readonly IDiagnosticContext _requestLogLine;
    private readonly ILogger<RequestAuditor> _logger;
    private readonly Stopwatch _stopwatch = new();
    private RequestLog _record = null!;

    public RequestAuditor(ProxyDbContext db, IDiagnosticContext requestLogLine, ILogger<RequestAuditor> logger)
    {
        _db = db;
        _requestLogLine = requestLogLine;
        _logger = logger;
    }

    public void Begin(string correlationId)
    {
        _record = new RequestLog { CorrelationId = correlationId, StartedAt = DateTime.UtcNow };
        _stopwatch.Start();
    }

    /// <summary>
    /// What the checks learned. When the operation was found, its key comes from the configuration;
    /// otherwise it is the caller's text, with unsafe characters replaced (decision 14).
    /// </summary>
    public void SetOperation(string? requestedKey, OperationPolicy? operation)
    {
        _record.OperationKey = operation?.Key ?? SafeOperationKey(requestedKey);
        _record.DestinationName = operation?.Destination.Name;
    }

    /// <summary>The mandatory save before forwarding. False means: do not forward.</summary>
    public async Task<bool> TrySaveBeforeForwardAsync()
    {
        _record.Outcome = RequestOutcome.InProgress;
        try
        {
            _db.RequestLogs.Add(_record);
            // Not the caller's token: once we decided to forward, the record must be written even if the caller leaves.
            await _db.SaveChangesAsync(CancellationToken.None);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Audit record could not be saved before forwarding; the request is not forwarded");
            return false;
        }
    }

    /// <summary>The request was sent to the destination. The status is null when the destination did not answer.</summary>
    public void SetForwarded(int? upstreamStatusCode)
    {
        _record.WasForwarded = true;
        _record.UpstreamStatusCode = upstreamStatusCode;
    }

    /// <summary>Reason is a fixed code (an enum name), never text from the caller. ProxyStatusCode is null when nobody was answered.</summary>
    public void SetResult(RequestOutcome outcome, string? reason, int? proxyStatusCode)
    {
        _record.Outcome = outcome;
        _record.Reason = reason;
        _record.ProxyStatusCode = proxyStatusCode;
    }

    /// <summary>The best-effort save at the end. Never throws: the audit must not break the answer.</summary>
    public async Task CompleteAsync()
    {
        _record.CompletedAt = DateTime.UtcNow;
        _record.DurationMs = _stopwatch.ElapsedMilliseconds;

        // The same fields on the request's summary line in the technical log.
        _requestLogLine.Set("OperationKey", _record.OperationKey);
        _requestLogLine.Set("Outcome", _record.Outcome);
        _requestLogLine.Set("Reason", _record.Reason);

        try
        {
            // A new row (rejections), or the row saved before forwarding (EF tracks it and sends an UPDATE).
            if (_db.Entry(_record).State == EntityState.Detached)
                _db.RequestLogs.Add(_record);
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            // The fallback: the whole record, as structured fields, in the technical log.
            _logger.LogError(exception,
                "Audit record could not be saved: {CorrelationId} {OperationKey} {DestinationName} {StartedAt} " +
                "{DurationMs} {WasForwarded} {UpstreamStatusCode} {ProxyStatusCode} {Outcome} {Reason}",
                _record.CorrelationId, _record.OperationKey, _record.DestinationName, _record.StartedAt,
                _record.DurationMs, _record.WasForwarded, _record.UpstreamStatusCode, _record.ProxyStatusCode,
                _record.Outcome, _record.Reason);
        }
    }

    /// <summary>
    /// The caller's key, safe to store and to log: anything but letters, digits, '.', '_' and '-' becomes '?'
    /// (no line breaks or markup in the logs). The length is already limited to 100 by the envelope check.
    /// </summary>
    public static string? SafeOperationKey(string? requestedKey) =>
        requestedKey is null
            ? null
            : new string(requestedKey.Select(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '?').ToArray());
}
