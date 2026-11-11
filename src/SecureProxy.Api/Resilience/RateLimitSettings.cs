namespace SecureProxy.Api.Resilience;

/// <summary>
/// Limits on how much work the proxy accepts (assignment section 6, Resilience), read from the
/// "RateLimiting" section of appsettings. Not from the database: the limiters exist before any request.
/// </summary>
public sealed class RateLimitSettings
{
    public const string SectionName = "RateLimiting";

    /// <summary>Proxy requests one client (IP address) may send per minute.</summary>
    public int ProxyRequestsPerMinute { get; set; } = 100;

    /// <summary>Admin requests one client may send per minute. Low, so the admin key cannot be guessed by trying.</summary>
    public int AdminRequestsPerMinute { get; set; } = 10;

    /// <summary>Requests the whole application handles at the same time. Beyond that, new requests are rejected at once.</summary>
    public int MaxConcurrentRequests { get; set; } = 100;
}
