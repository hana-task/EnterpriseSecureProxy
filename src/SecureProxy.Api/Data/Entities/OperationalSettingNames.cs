namespace SecureProxy.Api.Data.Entities;

/// <summary>The setting names the code reads, kept in one place to avoid typos.</summary>
public static class OperationalSettingNames
{
    /// <summary>Largest request accepted at all, checked before the operation is known.</summary>
    public const string GlobalMaxRequestBytes = "GlobalMaxRequestBytes";

    /// <summary>How many seconds operation configuration is kept in the memory cache.</summary>
    public const string ConfigurationCacheSeconds = "ConfigurationCacheSeconds";
}
