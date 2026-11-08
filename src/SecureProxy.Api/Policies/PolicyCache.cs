using Microsoft.Extensions.Caching.Memory;

namespace SecureProxy.Api.Policies;

/// <summary>
/// In-memory storage for configuration. Registered as a singleton, so all requests share it.
/// It owns a dedicated MemoryCache, so Clear() removes configuration and nothing else.
/// </summary>
public sealed class PolicyCache : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public bool TryGet<T>(string key, out T? value) => _cache.TryGetValue(key, out value);

    public void Set<T>(string key, T value, TimeSpan timeToLive) => _cache.Set(key, value, timeToLive);

    /// <summary>Removes everything. Called after any configuration update.</summary>
    public void Clear() => _cache.Clear();

    public void Dispose() => _cache.Dispose();
}
