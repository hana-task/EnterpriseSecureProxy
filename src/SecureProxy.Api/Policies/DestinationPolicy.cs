namespace SecureProxy.Api.Policies;

/// <summary>Read-only copy of the destination an operation points to.</summary>
public sealed record DestinationPolicy
{
    public required string Name { get; init; }
    public required string BaseUrl { get; init; }
    public required bool IsEnabled { get; init; }
    public required int TimeoutSeconds { get; init; }
    public required long MaxResponseBytes { get; init; }
}
