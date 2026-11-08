namespace SecureProxy.Api.Policies;

/// <summary>
/// The single place the proxy gets its configuration from. Serves from the cache when possible,
/// otherwise reads from the database.
/// </summary>
public interface IPolicyProvider
{
    /// <summary>Returns the operation with the exact given key, or null if there is none.</summary>
    Task<OperationPolicy?> FindOperationAsync(string operationKey, CancellationToken cancellationToken);

    Task<ProxySettings> GetSettingsAsync(CancellationToken cancellationToken);

    /// <summary>Drops all cached configuration so the next request reads fresh values.</summary>
    void InvalidateAll();
}
