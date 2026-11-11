using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SecureProxy.Api.Errors;

namespace SecureProxy.Api.Resilience;

/// <summary>
/// Rate limiting and bounded concurrency, with the limiter built into ASP.NET Core (no extra package).
/// - Every request first takes a place from one shared concurrency limit (MaxConcurrentRequests).
/// - Then the endpoint's own policy counts the client's requests per minute: "proxy" or "admin".
/// A rejected request never reaches a controller: no database work, nothing sent to a destination.
/// </summary>
public static class RateLimitingSetup
{
    public const string ProxyPolicy = "proxy";
    public const string AdminPolicy = "admin";

    public static IServiceCollection AddProxyRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitSettings>(configuration.GetSection(RateLimitSettings.SectionName));

        services.AddRateLimiter(options =>
        {
            // One partition for the whole application, so this is one counter of requests in progress.
            // QueueLimit 0: when it is full, a request is rejected at once instead of waiting.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetConcurrencyLimiter("all", _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = SettingsOf(context).MaxConcurrentRequests,
                    QueueLimit = 0
                }));

            options.AddPolicy(ProxyPolicy, context =>
                PerClientPerMinute(context, SettingsOf(context).ProxyRequestsPerMinute));
            options.AddPolicy(AdminPolicy, context =>
                PerClientPerMinute(context, SettingsOf(context).AdminRequestsPerMinute));

            options.OnRejected = WriteRejectionAsync;
        });

        return services;
    }

    /// <summary>
    /// A fixed one-minute window per client. The client is its IP address, because the proxy has no caller
    /// identity (documented). Behind a gateway this would be the forwarded client address or the caller's identity.
    /// </summary>
    private static RateLimitPartition<string> PerClientPerMinute(HttpContext context, int permitsPerMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });

    /// <summary>
    /// Read when a limiter is created (not at startup), so a test can set its own values.
    /// The settings are a singleton, so they are not taken from the request's scope.
    /// </summary>
    private static RateLimitSettings SettingsOf(HttpContext context) =>
        context.RequestServices.GetRequiredService<IOptions<RateLimitSettings>>().Value;

    /// <summary>
    /// 429 in the proxy's usual error format. Not written to RequestLogs: during a flood that would turn
    /// every rejected request into a database write. One warning line in the technical log instead.
    /// </summary>
    private static async ValueTask WriteRejectionAsync(OnRejectedContext rejected, CancellationToken cancellationToken)
    {
        var context = rejected.HttpContext;

        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RateLimitingSetup).FullName!)
            .LogWarning("Request rejected by the rate limiter: {Method} {Path}", context.Request.Method, context.Request.Path);

        // Only the per-minute limiters know when to come back; the concurrency limit does not.
        // Added just before the headers are sent, because ProxyErrorWriter first clears the response.
        if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                return Task.CompletedTask;
            });
        }

        await ProxyErrorWriter.WriteAsync(context.Response, ProxyErrorCatalog.TooManyRequests);
    }
}
