namespace SecureProxy.Api.Forwarding;

/// <summary>How the attempt to reach the destination ended.</summary>
public enum ForwardOutcome
{
    /// <summary>The destination answered. Any status code, including 4xx and 5xx.</summary>
    Responded
}
