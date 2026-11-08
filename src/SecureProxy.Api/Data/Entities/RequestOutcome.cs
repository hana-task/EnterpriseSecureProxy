namespace SecureProxy.Api.Data.Entities;

/// <summary>The outcome category of one proxy request (assignment 5.6).</summary>
public enum RequestOutcome
{
    /// <summary>Forwarded, and the upstream answered with a success status.</summary>
    Success,

    /// <summary>Failed a validation or policy check. Never forwarded.</summary>
    Rejected,

    /// <summary>Forwarded, and the upstream answered with an error status (4xx/5xx).</summary>
    UpstreamError,

    /// <summary>Forwarded, and the upstream did not answer within the configured timeout.</summary>
    UpstreamTimeout,

    /// <summary>The upstream could not be reached (connection failure).</summary>
    UpstreamUnavailable,

    /// <summary>The caller cancelled (disconnected) before the request completed.</summary>
    CallerCancelled,

    /// <summary>An unexpected failure inside the proxy.</summary>
    ProxyError
}
