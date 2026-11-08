namespace SecureProxy.Api.Security.Headers;

/// <summary>
/// Chooses which of the caller's headers are forwarded. A header is forwarded only if:
/// it is on the operation's allowlist, AND it is not on the fixed forbidden list,
/// AND it is not named in the Connection header (those are hop-by-hop too).
/// Everything else is silently dropped; the request itself is not rejected.
/// </summary>
public static class HeaderFilter
{
    public static IReadOnlyList<KeyValuePair<string, string>> Filter(
        IHeaderDictionary incomingHeaders, IReadOnlyList<string> allowedHeaderNames)
    {
        var allowed = new HashSet<string>(allowedHeaderNames, StringComparer.OrdinalIgnoreCase);
        var namedInConnection = ReadConnectionHeaderNames(incomingHeaders);
        var forwarded = new List<KeyValuePair<string, string>>();

        foreach (var header in incomingHeaders)
        {
            if (!allowed.Contains(header.Key)
                || ForbiddenHeaders.Contains(header.Key)
                || namedInConnection.Contains(header.Key))
                continue;

            var value = string.Join(", ", header.Value.ToArray());

            // A line break in a value could inject extra headers into the upstream request.
            if (value.Contains('\r') || value.Contains('\n'))
                continue;

            forwarded.Add(new KeyValuePair<string, string>(header.Key, value));
        }

        return forwarded;
    }

    /// <summary>"Connection: close, X-Custom" means X-Custom is meant for this connection only.</summary>
    private static HashSet<string> ReadConnectionHeaderNames(IHeaderDictionary headers)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in headers.Connection.ToArray())
        {
            foreach (var name in (line ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                names.Add(name);
        }
        return names;
    }
}
