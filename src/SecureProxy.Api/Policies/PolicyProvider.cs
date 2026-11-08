using SecureProxy.Api.Data.Repositories;

namespace SecureProxy.Api.Policies;

/// <summary>
/// Cache in front of the repository ("cache-aside"): look in the cache, and on a miss read the
/// database and store the result for ConfigurationCacheSeconds.
///
/// Consistency model: a change made through the admin API clears the cache at once on this
/// instance. Any other change (another instance, direct SQL) becomes visible within
/// ConfigurationCacheSeconds at the latest.
/// </summary>
public sealed class PolicyProvider : IPolicyProvider
{
    private const string SettingsCacheKey = "settings";
    private const string OperationCacheKeyPrefix = "operation:";

    private readonly IPolicyRepository _repository;
    private readonly PolicyCache _cache;

    public PolicyProvider(IPolicyRepository repository, PolicyCache cache)
    {
        _repository = repository;
        _cache = cache;
    }

    public async Task<OperationPolicy?> FindOperationAsync(string operationKey, CancellationToken cancellationToken)
    {
        var cacheKey = OperationCacheKeyPrefix + operationKey;
        if (_cache.TryGet(cacheKey, out OperationPolicy? cachedPolicy))
            return cachedPolicy;

        var policy = await _repository.FindOperationPolicyAsync(operationKey, cancellationToken);

        // Unknown keys are not cached: the key comes from the caller, so caching "not found"
        // would let anyone fill the memory by sending random keys.
        if (policy is null)
            return null;

        var settings = await GetSettingsAsync(cancellationToken);
        StoreInCache(cacheKey, policy, settings.ConfigurationCacheSeconds);
        return policy;
    }

    public async Task<ProxySettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGet(SettingsCacheKey, out ProxySettings? cachedSettings) && cachedSettings is not null)
            return cachedSettings;

        var settings = await _repository.GetProxySettingsAsync(cancellationToken);
        StoreInCache(SettingsCacheKey, settings, settings.ConfigurationCacheSeconds);
        return settings;
    }

    public void InvalidateAll() => _cache.Clear();

    private void StoreInCache<T>(string cacheKey, T value, int cacheSeconds)
    {
        // 0 seconds means caching is turned off.
        if (cacheSeconds > 0)
            _cache.Set(cacheKey, value, TimeSpan.FromSeconds(cacheSeconds));
    }
}
