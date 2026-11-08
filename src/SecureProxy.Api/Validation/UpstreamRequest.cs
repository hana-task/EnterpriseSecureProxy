namespace SecureProxy.Api.Validation;

/// <summary>What will be sent to the destination: built only from configuration and validated input.</summary>
public sealed record UpstreamRequest(string HttpMethod, Uri Url, string? BodyJson)
{
    /// <summary>The caller's headers that passed the filter. Set by the validator after the headers are filtered.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = Array.Empty<KeyValuePair<string, string>>();
}
