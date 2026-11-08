namespace SecureProxy.Api.Policies;

/// <summary>Proxy-wide settings, read from the OperationalSettings table.</summary>
public sealed record ProxySettings
{
    /// <summary>Used when a setting is missing or invalid. Safe, restrictive values.</summary>
    public static readonly ProxySettings Defaults = new()
    {
        GlobalMaxRequestBytes = 65_536,
        ConfigurationCacheSeconds = 30
    };

    /// <summary>Largest request accepted at all, checked before the operation is known.</summary>
    public required int GlobalMaxRequestBytes { get; init; }

    /// <summary>How long configuration stays in the cache. 0 disables caching.</summary>
    public required int ConfigurationCacheSeconds { get; init; }
}
