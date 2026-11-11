using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureProxy.Api.Admin;
using SecureProxy.Api.Data;
using SecureProxy.Api.Data.Entities;

namespace SecureProxy.Tests.Integration;

/// <summary>
/// The admin API (configuration updates): test 16 (concurrency conflict → 409), cache invalidation,
/// the documented consistency model, configuration history and the admin key.
/// Each test changes only its own operation (ProxyApplicationFactory.AdminTestOperationKeys).
/// </summary>
public partial class ProxyEndpointTests
{
    private const string AdminEndpoint = "/api/admin/operations/";

    private Task<HttpResponseMessage> AdminGetAsync(string operationKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, AdminEndpoint + operationKey);
        request.Headers.Add(AdminKeyFilter.HeaderName, ProxyApplicationFactory.AdminKey);
        return _caller.SendAsync(request);
    }

    /// <summary>A PUT as an admin client sends it. adminKey null = no X-Admin-Key header at all.</summary>
    private Task<HttpResponseMessage> AdminPutAsync(string operationKey, bool isEnabled, int maxRequestBytes,
        byte[] rowVersion, string? adminKey = ProxyApplicationFactory.AdminKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, AdminEndpoint + operationKey)
        {
            // byte[] is written as base64, exactly as GET returned it.
            Content = JsonContent.Create(new { isEnabled, maxRequestBytes, rowVersion })
        };
        if (adminKey is not null)
            request.Headers.Add(AdminKeyFilter.HeaderName, adminKey);
        return _caller.SendAsync(request);
    }

    /// <summary>GET, as an admin client does before an update: returns the current version (rowVersion).</summary>
    private async Task<OperationSettings> ReadSettingsAsync(string operationKey)
    {
        var response = await AdminGetAsync(operationKey);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OperationSettings>())!;
    }

    /// <summary>The row as it is in SQL Server now (a fresh DbContext, not the app's cache).</summary>
    private async Task<Operation> OperationRowAsync(string operationKey)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();
        return await db.Operations.AsNoTracking().SingleAsync(operation => operation.Key == operationKey);
    }

    private Task<HttpResponseMessage> SendProxyRequestAsync(string operationKey) =>
        SendAsync(JsonSerializer.Serialize(new
        {
            clientId = operationKey,
            json = new { urlId = ProxyApplicationFactory.UrlIdOf(operationKey) }
        }));

    private static async Task AssertProxyErrorAsync(HttpResponseMessage response, HttpStatusCode statusCode, string errorCode)
    {
        Assert.Equal(statusCode, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(errorCode, problem.GetProperty("errorCode").GetString());
    }

    // ---- 16. configuration concurrency conflict ----

    [Fact]
    public async Task Update_based_on_an_old_version_is_rejected_with_409_and_changes_nothing()
    {
        const string key = "test.admin.conflict";
        var original = await ReadSettingsAsync(key); // both admins read the same version

        // The first admin saves: accepted, and SQL Server gives the row a new version.
        var first = await AdminPutAsync(key, isEnabled: true, maxRequestBytes: 2048, original.RowVersion);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var afterFirst = (await first.Content.ReadFromJsonAsync<OperationSettings>())!;
        Assert.NotEqual(original.RowVersion, afterFirst.RowVersion);

        // The second admin saves with the version read before the first update: rejected.
        var second = await AdminPutAsync(key, isEnabled: true, maxRequestBytes: 1024, original.RowVersion);
        await AssertProxyErrorAsync(second, HttpStatusCode.Conflict, "concurrency_conflict");

        // The database keeps the first update; the stale one did not overwrite it.
        var row = await OperationRowAsync(key);
        Assert.Equal(2048, row.MaxRequestBytes);
        Assert.Equal(afterFirst.RowVersion, row.RowVersion);
    }

    [Fact]
    public async Task Stale_update_with_unchanged_values_is_still_rejected()
    {
        // The values sent are the current ones, so no column but UpdatedAt changes. Still a conflict:
        // OperationAdminService always changes UpdatedAt, so EF always sends the UPDATE with the version check.
        const string key = "test.admin.unchanged";
        var current = await ReadSettingsAsync(key);
        var update = await AdminPutAsync(key, current.IsEnabled, current.MaxRequestBytes, current.RowVersion);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var stale = await AdminPutAsync(key, current.IsEnabled, current.MaxRequestBytes, current.RowVersion);

        await AssertProxyErrorAsync(stale, HttpStatusCode.Conflict, "concurrency_conflict");
    }

    // ---- cache: cleared by an admin update; a direct SQL change waits for the expiration ----

    [Fact]
    public async Task Operation_disabled_through_the_admin_api_is_rejected_at_once()
    {
        const string key = "test.admin.cache";
        Assert.Equal(HttpStatusCode.OK, (await SendProxyRequestAsync(key)).StatusCode); // the policy is now cached
        _destination.Reset(HttpStatusCode.OK);

        var settings = await ReadSettingsAsync(key);
        var update = await AdminPutAsync(key, isEnabled: false, settings.MaxRequestBytes, settings.RowVersion);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        // Without the cache being cleared, the cached "enabled" policy would still be used for up to 30 seconds.
        var response = await SendProxyRequestAsync(key);
        await AssertRejectedByProxyAsync(response, HttpStatusCode.Forbidden, "operation_disabled");
    }

    [Fact]
    public async Task Change_made_directly_in_sql_waits_for_the_cache_to_expire()
    {
        // The documented consistency model: a change that bypasses the admin API is seen within
        // ConfigurationCacheSeconds (30 s in the seed), not at once. This shows the "not at once" part.
        const string key = "test.admin.sql";
        Assert.Equal(HttpStatusCode.OK, (await SendProxyRequestAsync(key)).StatusCode); // cached as enabled

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();
            await db.Operations.Where(operation => operation.Key == key)
                .ExecuteUpdateAsync(setters => setters.SetProperty(operation => operation.IsEnabled, false));
        }

        var response = await SendProxyRequestAsync(key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // still the cached policy
        Assert.Equal(2, _destination.Requests.Count);
    }

    // ---- configuration history (assignment 5.7): the temporal table keeps the previous version ----

    [Fact]
    public async Task Update_keeps_the_previous_version_in_the_history_table()
    {
        const string key = "test.admin.history";
        var settings = await ReadSettingsAsync(key);

        var update = await AdminPutAsync(key, isEnabled: false, settings.MaxRequestBytes, settings.RowVersion);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();
        // TemporalAll = the current row and all its previous versions, each with the time range it was valid.
        var versions = await db.Operations.TemporalAll()
            .Where(operation => operation.Key == key)
            .OrderBy(operation => EF.Property<DateTime>(operation, "PeriodStart"))
            .Select(operation => operation.IsEnabled)
            .ToListAsync();

        Assert.Equal(new[] { true, false }, versions); // before and after the update
    }

    // ---- admin key and unknown operation ----

    [Theory]
    [InlineData(null)]          // no X-Admin-Key header
    [InlineData("wrong-key")]
    public async Task Update_without_the_right_admin_key_is_rejected_and_changes_nothing(string? adminKey)
    {
        const string key = "test.admin.auth";
        var before = await OperationRowAsync(key);

        var response = await AdminPutAsync(key, isEnabled: false, maxRequestBytes: 1, before.RowVersion, adminKey);

        await AssertProxyErrorAsync(response, HttpStatusCode.Unauthorized, "admin_unauthorized");
        var after = await OperationRowAsync(key);
        Assert.True(after.IsEnabled);
        Assert.Equal(before.RowVersion, after.RowVersion);
    }

    [Fact]
    public async Task Update_of_an_unknown_operation_is_rejected_with_404()
    {
        var response = await AdminPutAsync("test.admin.missing", isEnabled: false, maxRequestBytes: 1, new byte[8]);

        await AssertProxyErrorAsync(response, HttpStatusCode.NotFound, "unknown_operation");
    }

    [Fact]
    public async Task Field_that_cannot_be_updated_is_rejected_not_ignored()
    {
        // An admin who sends httpMethod must not get 200 and believe the method changed.
        const string key = "test.admin.auth";
        var before = await OperationRowAsync(key);
        var request = new HttpRequestMessage(HttpMethod.Put, AdminEndpoint + key)
        {
            Content = new StringContent(
                $$"""{"isEnabled":false,"maxRequestBytes":1,"rowVersion":"{{Convert.ToBase64String(before.RowVersion)}}","httpMethod":"POST"}""",
                System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add(AdminKeyFilter.HeaderName, ProxyApplicationFactory.AdminKey);

        var response = await _caller.SendAsync(request);

        await AssertProxyErrorAsync(response, HttpStatusCode.BadRequest, "invalid_admin_request");
        Assert.Equal(before.RowVersion, (await OperationRowAsync(key)).RowVersion);
    }

    [Fact]
    public async Task Admin_api_matches_the_operation_key_exactly()
    {
        // SQL Server would match "TEST.admin.auth" to "test.admin.auth"; the admin API must not.
        var response = await AdminGetAsync("TEST.admin.auth");

        await AssertProxyErrorAsync(response, HttpStatusCode.NotFound, "unknown_operation");
    }
}
