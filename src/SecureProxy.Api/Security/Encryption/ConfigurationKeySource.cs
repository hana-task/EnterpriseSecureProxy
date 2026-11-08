namespace SecureProxy.Api.Security.Encryption;

/// <summary>
/// Local/assessment key source: reads base64-encoded 256-bit keys from configuration
/// under "ProxyKeys:{reference}". In development the values come from dotnet user-secrets;
/// in a deployed environment from environment variables (ProxyKeys__{reference}).
/// A production deployment would swap this for a vault-backed implementation
/// (Azure Key Vault, HashiCorp Vault, etc.) without touching the rest of the pipeline.
/// </summary>
public sealed class ConfigurationKeySource : IKeySource
{
    public const string SectionName = "ProxyKeys";
    private const int RequiredKeySizeBytes = 32; // AES-256

    private readonly IConfiguration _configuration;

    public ConfigurationKeySource(IConfiguration configuration) => _configuration = configuration;

    public byte[]? GetKey(string keyReference)
    {
        if (string.IsNullOrWhiteSpace(keyReference))
            return null;

        var encoded = _configuration[$"{SectionName}:{keyReference}"];
        if (string.IsNullOrWhiteSpace(encoded))
            return null;

        try
        {
            var key = Convert.FromBase64String(encoded);
            return key.Length == RequiredKeySizeBytes ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
