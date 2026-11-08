namespace SecureProxy.Api.Policies;

/// <summary>
/// Everything the proxy needs to know to execute one operation, read from configuration in one go.
///
/// This is an immutable read-only copy, not the EF entity: it is cached and shared by many
/// concurrent requests, so nothing may change it after it is created.
/// </summary>
public sealed record OperationPolicy
{
    public required string Key { get; init; }
    public required string UrlId { get; init; }
    public required bool IsEnabled { get; init; }
    public required string HttpMethod { get; init; }
    public required string PathTemplate { get; init; }
    public required int MaxRequestBytes { get; init; }
    public required bool RequiresEncryption { get; init; }

    /// <summary>Encryption algorithm name, or null when the operation has no encryption profile.</summary>
    public string? EncryptionAlgorithm { get; init; }

    /// <summary>Name of the key in the secret store, or null when the operation has no encryption profile.</summary>
    public string? EncryptionKeyReference { get; init; }

    public required bool AllowUnknownParameters { get; init; }
    public required DestinationPolicy Destination { get; init; }
    public required IReadOnlyList<ParameterPolicy> Parameters { get; init; }

    /// <summary>Incoming header names that may be forwarded (the allowlist).</summary>
    public required IReadOnlyList<string> AllowedHeaders { get; init; }
}
