using SecureProxy.Api.Policies;

namespace SecureProxy.Api.Data.Repositories;

/// <summary>Reads proxy configuration from the database. No caching here; see PolicyProvider.</summary>
public interface IPolicyRepository
{
    /// <summary>Returns the operation with the exact given key, or null if there is none.</summary>
    Task<OperationPolicy?> FindOperationPolicyAsync(string operationKey, CancellationToken cancellationToken);

    Task<ProxySettings> GetProxySettingsAsync(CancellationToken cancellationToken);
}
