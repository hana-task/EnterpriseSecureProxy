using SecureProxy.Api.Data.Repositories;
using SecureProxy.Api.Policies;

namespace SecureProxy.Tests.Policies;

public class PolicyProviderTests
{
    /// <summary>A fake repository that returns fixed data and counts how often it is asked.</summary>
    private sealed class CountingRepository : IPolicyRepository
    {
        public int OperationReads { get; private set; }
        public ProxySettings Settings { get; set; } = new() { GlobalMaxRequestBytes = 1000, ConfigurationCacheSeconds = 30 };

        public Task<OperationPolicy?> FindOperationPolicyAsync(string operationKey, CancellationToken cancellationToken)
        {
            OperationReads++;
            return Task.FromResult(operationKey == "customer.get" ? CreatePolicy(operationKey) : null);
        }

        public Task<ProxySettings> GetProxySettingsAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);
    }

    private static OperationPolicy CreatePolicy(string key) => new()
    {
        Key = key,
        UrlId = "crm-customers",
        IsEnabled = true,
        HttpMethod = "GET",
        PathTemplate = "/customers/{customerId}",
        MaxRequestBytes = 4096,
        RequiresEncryption = false,
        AllowUnknownParameters = false,
        Destination = new DestinationPolicy
        {
            Name = "crm", BaseUrl = "http://localhost", IsEnabled = true, TimeoutSeconds = 10, MaxResponseBytes = 1000
        },
        Parameters = Array.Empty<ParameterPolicy>(),
        AllowedHeaders = Array.Empty<string>()
    };

    private readonly CountingRepository _repository = new();
    private readonly PolicyCache _cache = new();

    private PolicyProvider CreateProvider() => new(_repository, _cache);

    [Fact]
    public async Task Second_request_is_served_from_cache()
    {
        var provider = CreateProvider();

        await provider.FindOperationAsync("customer.get", CancellationToken.None);
        var second = await provider.FindOperationAsync("customer.get", CancellationToken.None);

        Assert.NotNull(second);
        Assert.Equal(1, _repository.OperationReads);
    }

    [Fact]
    public async Task Cache_is_shared_between_requests()
    {
        // Each HTTP request gets its own provider (scoped), but they share the singleton cache.
        await CreateProvider().FindOperationAsync("customer.get", CancellationToken.None);
        await CreateProvider().FindOperationAsync("customer.get", CancellationToken.None);

        Assert.Equal(1, _repository.OperationReads);
    }

    [Fact]
    public async Task InvalidateAll_forces_a_fresh_read()
    {
        var provider = CreateProvider();
        await provider.FindOperationAsync("customer.get", CancellationToken.None);

        provider.InvalidateAll();
        await provider.FindOperationAsync("customer.get", CancellationToken.None);

        Assert.Equal(2, _repository.OperationReads);
    }

    [Fact]
    public async Task Unknown_operation_is_not_cached()
    {
        var provider = CreateProvider();

        var first = await provider.FindOperationAsync("no.such.operation", CancellationToken.None);
        var second = await provider.FindOperationAsync("no.such.operation", CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(2, _repository.OperationReads);
    }

    [Fact]
    public async Task Zero_cache_seconds_disables_caching()
    {
        _repository.Settings = new ProxySettings { GlobalMaxRequestBytes = 1000, ConfigurationCacheSeconds = 0 };
        var provider = CreateProvider();

        await provider.FindOperationAsync("customer.get", CancellationToken.None);
        await provider.FindOperationAsync("customer.get", CancellationToken.None);

        Assert.Equal(2, _repository.OperationReads);
    }
}
