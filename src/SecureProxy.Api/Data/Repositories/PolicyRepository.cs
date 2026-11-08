using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Data.Entities;
using SecureProxy.Api.Policies;

namespace SecureProxy.Api.Data.Repositories;

public sealed class PolicyRepository : IPolicyRepository
{
    private readonly ProxyDbContext _db;
    private readonly ILogger<PolicyRepository> _logger;

    public PolicyRepository(ProxyDbContext db, ILogger<PolicyRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<OperationPolicy?> FindOperationPolicyAsync(string operationKey, CancellationToken cancellationToken)
    {
        // One SQL query that reads the operation together with its destination, rules and headers,
        // and copies the values straight into the read-only policy objects.
        // AsNoTracking: we only read; EF does not need to watch these rows for changes.
        var policy = await _db.Operations
            .AsNoTracking()
            .Where(operation => operation.Key == operationKey)
            .Select(operation => new OperationPolicy
            {
                Key = operation.Key,
                UrlId = operation.UrlId,
                IsEnabled = operation.IsEnabled,
                HttpMethod = operation.HttpMethod,
                PathTemplate = operation.PathTemplate,
                MaxRequestBytes = operation.MaxRequestBytes,
                RequiresEncryption = operation.RequiresEncryption,
                EncryptionAlgorithm = operation.EncryptionProfile != null ? operation.EncryptionProfile.Algorithm : null,
                EncryptionKeyReference = operation.EncryptionProfile != null ? operation.EncryptionProfile.KeyReference : null,
                AllowUnknownParameters = operation.AllowUnknownParameters,
                Destination = new DestinationPolicy
                {
                    Name = operation.Destination.Name,
                    BaseUrl = operation.Destination.BaseUrl,
                    IsEnabled = operation.Destination.IsEnabled,
                    TimeoutSeconds = operation.Destination.TimeoutSeconds,
                    MaxResponseBytes = operation.Destination.MaxResponseBytes
                },
                Parameters = operation.ParameterRules
                    .Select(rule => new ParameterPolicy
                    {
                        Name = rule.Name,
                        Location = rule.Location,
                        DataType = rule.DataType,
                        IsRequired = rule.IsRequired,
                        MaxLength = rule.MaxLength,
                        MinValue = rule.MinValue,
                        MaxValue = rule.MaxValue
                    })
                    .ToList(),
                AllowedHeaders = operation.HeaderRules
                    .Select(rule => rule.HeaderName)
                    .ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        // SQL Server compares text case-insensitively by default, so "Customer.GET" would match
        // "customer.get". Operation keys must match exactly, so we check again in code.
        if (policy is null || !string.Equals(policy.Key, operationKey, StringComparison.Ordinal))
            return null;

        return policy;
    }

    public async Task<ProxySettings> GetProxySettingsAsync(CancellationToken cancellationToken)
    {
        var values = await _db.OperationalSettings
            .AsNoTracking()
            .ToDictionaryAsync(setting => setting.Name, setting => setting.Value, cancellationToken);

        return new ProxySettings
        {
            GlobalMaxRequestBytes = ReadInt(values, OperationalSettingNames.GlobalMaxRequestBytes,
                minimum: 1, maximum: 10_485_760, ProxySettings.Defaults.GlobalMaxRequestBytes),
            ConfigurationCacheSeconds = ReadInt(values, OperationalSettingNames.ConfigurationCacheSeconds,
                minimum: 0, maximum: 3600, ProxySettings.Defaults.ConfigurationCacheSeconds)
        };
    }

    /// <summary>
    /// Reads a whole-number setting. A missing or invalid value falls back to the safe default
    /// and is logged, so a bad row never disables a limit.
    /// </summary>
    private int ReadInt(Dictionary<string, string> values, string name, int minimum, int maximum, int defaultValue)
    {
        if (values.TryGetValue(name, out var text)
            && int.TryParse(text, out var value)
            && value >= minimum && value <= maximum)
        {
            return value;
        }

        _logger.LogWarning("Operational setting {SettingName} is missing or invalid; using default {DefaultValue}",
            name, defaultValue);
        return defaultValue;
    }
}
