namespace SecureProxy.Api.Security.Headers;

/// <summary>
/// Headers that are never forwarded to the destination, whatever the database says.
/// This list is fixed in code on purpose: a wrong row in the HeaderRules table must not be able
/// to leak credentials or confuse the upstream.
/// </summary>
public static class ForbiddenHeaders
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        // Sensitive: they identify or authenticate the caller, not the proxy.
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-Api-Key",

        // Hop-by-hop: they describe one connection only and must not be passed on (RFC 9110),
        // plus the old non-standard Proxy-Connection and Expect (a connection-level handshake).
        "Connection", "Keep-Alive", "Proxy-Authenticate", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        "Proxy-Connection", "Expect",

        // Set by the proxy itself for the new request.
        "Host"
    };

    /// <summary>
    /// Also every "Content-*" header: they describe the caller's body (length, type, encoding...),
    /// and the proxy sends a body it built itself, so they would be wrong or misleading.
    /// </summary>
    public static bool Contains(string headerName) =>
        Names.Contains(headerName) || headerName.StartsWith("Content-", StringComparison.OrdinalIgnoreCase);
}
