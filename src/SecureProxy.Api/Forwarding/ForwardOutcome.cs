namespace SecureProxy.Api.Forwarding;

/// <summary>How the attempt to reach the destination ended.</summary>
public enum ForwardOutcome
{
    /// <summary>The destination answered. Any status code, including 4xx and 5xx.</summary>
    Responded,

    /// <summary>The destination did not answer within its configured timeout. Becomes 504.</summary>
    Timeout,

    /// <summary>The destination could not be reached (refused, DNS failure, connection dropped). Becomes 502.</summary>
    ConnectionFailed,

    /// <summary>The caller gave up (closed the connection). Nobody is waiting for an answer.</summary>
    CallerCancelled
}
