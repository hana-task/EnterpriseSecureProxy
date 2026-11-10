namespace SecureProxy.Api.Data.Entities;

/// <summary>
/// One audit record per request that reached the proxy, whatever the result (assignment 5.6).
/// Written explicitly by our code (RequestAuditor), not by the logging framework.
/// Never stores bodies, headers, keys, credentials or personal data.
/// No foreign keys on purpose: the log must survive configuration changes and must also
/// record requests for operations that do not exist.
/// </summary>
public class RequestLog
{
    public long Id { get; set; }

    /// <summary>Unique id of the request, also returned to the caller.</summary>
    public string CorrelationId { get; set; } = null!;

    /// <summary>The operation key the caller asked for. Null when the envelope was invalid.</summary>
    public string? OperationKey { get; set; }

    /// <summary>Destination name, when the operation was found.</summary>
    public string? DestinationName { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public long? DurationMs { get; set; }

    /// <summary>Whether the proxy sent the request to the destination. Set only after sending, never before.</summary>
    public bool WasForwarded { get; set; }

    /// <summary>Status returned by the upstream, when it answered.</summary>
    public int? UpstreamStatusCode { get; set; }

    /// <summary>Status the proxy returned to the caller.</summary>
    public int? ProxyStatusCode { get; set; }

    public RequestOutcome? Outcome { get; set; }

    /// <summary>A fixed reason code such as "operation_disabled". Never free text from the caller.</summary>
    public string? Reason { get; set; }
}
