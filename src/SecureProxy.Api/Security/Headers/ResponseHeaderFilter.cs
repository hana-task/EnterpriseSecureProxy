namespace SecureProxy.Api.Security.Headers;

/// <summary>
/// Chooses which of the destination's response headers reach the caller.
///
/// This is an allowlist fixed in code: everything not listed is dropped. That keeps out
/// Set-Cookie, Server and X-Powered-By (they identify the internal system), Location (an internal
/// address), and all hop-by-hop headers (Connection, Transfer-Encoding...) which describe only the
/// connection between the proxy and the destination. Content-Length is not copied either: the
/// body is streamed, so the proxy's own connection decides how it is framed.
/// </summary>
public static class ResponseHeaderFilter
{
    private static readonly HashSet<string> AllowedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Encoding", "Content-Language",
        "Cache-Control", "ETag", "Last-Modified", "Retry-After"
    };

    public static IReadOnlyList<KeyValuePair<string, string>> Filter(HttpResponseMessage response)
    {
        var forwarded = new List<KeyValuePair<string, string>>();

        // HttpClient splits headers in two groups: those of the message and those of the body.
        var allHeaders = response.Headers.Concat(response.Content.Headers);
        foreach (var header in allHeaders)
        {
            if (!AllowedNames.Contains(header.Key))
                continue;

            var value = string.Join(", ", header.Value);

            // A line break in a value could inject extra headers into the caller's response.
            if (value.Contains('\r') || value.Contains('\n'))
                continue;

            forwarded.Add(new KeyValuePair<string, string>(header.Key, value));
        }

        return forwarded;
    }
}
