using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SecureProxy.Api.Admin;
using SecureProxy.Api.Resilience;

namespace SecureProxy.Tests.Integration;

/// <summary>
/// Rate limiting and bounded concurrency (section 6, Resilience). Each test starts its own copy of the app
/// with low limits, so the shared app (high limits) and the other tests are not affected.
/// </summary>
public partial class ProxyEndpointTests
{
    private const string ValidCustomerRequest =
        """{"clientId":"customer.get","json":{"urlId":"crm-customers","customerId":"123"}}""";

    /// <summary>A copy of the app with its own limits. Configure runs after the appsettings binding, so it wins.</summary>
    private WebApplicationFactory<Program> AppWithLimits(int proxyPerMinute = 1000, int adminPerMinute = 1000,
        int maxConcurrent = 1000) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<RateLimitSettings>(settings =>
            {
                settings.ProxyRequestsPerMinute = proxyPerMinute;
                settings.AdminRequestsPerMinute = adminPerMinute;
                settings.MaxConcurrentRequests = maxConcurrent;
            })));

    private static Task<HttpResponseMessage> PostAsync(HttpClient caller, string body) =>
        caller.PostAsync(Endpoint, new StringContent(body, Encoding.UTF8, "application/json"));

    [Fact]
    public async Task Request_over_the_per_minute_limit_is_rejected_with_429_and_not_forwarded()
    {
        using var app = AppWithLimits(proxyPerMinute: 2);
        var caller = app.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(caller, ValidCustomerRequest)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(caller, ValidCustomerRequest)).StatusCode);
        _destination.Reset(HttpStatusCode.OK);

        var response = await PostAsync(caller, ValidCustomerRequest);

        await AssertRejectedByProxyAsync(response, HttpStatusCode.TooManyRequests, "too_many_requests");
        Assert.True(response.Headers.Contains("Retry-After"));
        Assert.True(response.Headers.Contains("X-Correlation-ID"));
    }

    [Fact]
    public async Task Admin_limit_is_separate_from_the_proxy_limit()
    {
        using var app = AppWithLimits(adminPerMinute: 1);
        var caller = app.CreateClient();

        // A wrong key still counts, so the admin key cannot be guessed by trying many keys.
        var guess = new HttpRequestMessage(HttpMethod.Get, AdminEndpoint + "customer.get");
        guess.Headers.Add(AdminKeyFilter.HeaderName, "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await caller.SendAsync(guess)).StatusCode);

        var secondGuess = new HttpRequestMessage(HttpMethod.Get, AdminEndpoint + "customer.get");
        secondGuess.Headers.Add(AdminKeyFilter.HeaderName, ProxyApplicationFactory.AdminKey);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await caller.SendAsync(secondGuess)).StatusCode);

        // The proxy endpoint has its own counter and still works.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(caller, ValidCustomerRequest)).StatusCode);
    }

    [Fact]
    public async Task Request_beyond_the_concurrency_limit_is_rejected_while_another_is_in_progress()
    {
        using var app = AppWithLimits(maxConcurrent: 1);
        var caller = app.CreateClient();

        // The destination holds the first request until the test lets it go.
        var firstReachedDestination = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        _destination.Reset(async (_, _) =>
        {
            firstReachedDestination.TrySetResult();
            await releaseFirst.Task;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var first = PostAsync(caller, ValidCustomerRequest);
        await firstReachedDestination.Task.WaitAsync(TimeSpan.FromSeconds(5)); // the only place is now taken

        var second = await PostAsync(caller, ValidCustomerRequest);

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Single(_destination.Requests); // only the first one reached the destination

        // When the first one finishes, its place is free again.
        releaseFirst.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(caller, ValidCustomerRequest)).StatusCode);
    }
}
