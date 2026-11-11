using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SecureProxy.Api.Errors;

namespace SecureProxy.Api.Admin;

/// <summary>
/// Protects the admin API with a shared secret: the caller sends it in the X-Admin-Key header, and it must equal
/// the configured "Admin:ApiKey" (user-secrets in development, environment variable Admin__ApiKey elsewhere;
/// never in appsettings). A real deployment would use an identity provider (for example a JWT with an admin role).
///
/// Fail closed: when no admin key is configured, every admin request is rejected.
/// Runs before the action (an authorization filter), so a rejected request never reaches the admin code.
/// </summary>
public sealed class AdminKeyFilter : IAsyncAuthorizationFilter
{
    public const string HeaderName = "X-Admin-Key";
    public const string ConfigurationKey = "Admin:ApiKey";

    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminKeyFilter> _logger;

    public AdminKeyFilter(IConfiguration configuration, ILogger<AdminKeyFilter> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // Read on every request, so a rotated key takes effect without a restart.
        var configuredKey = _configuration[ConfigurationKey];
        if (string.IsNullOrEmpty(configuredKey))
        {
            _logger.LogWarning("Admin request rejected: no admin key is configured ({ConfigurationKey})", ConfigurationKey);
            await RejectAsync(context);
            return;
        }

        var sentKey = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (!KeysMatch(sentKey, configuredKey))
        {
            // The sent value is never logged: it may be a real key with a typo.
            _logger.LogWarning("Admin request rejected: missing or wrong admin key");
            await RejectAsync(context);
        }
    }

    /// <summary>
    /// Compares in constant time: a normal string comparison stops at the first different character,
    /// and the time it takes could reveal how much of the key was right.
    /// </summary>
    private static bool KeysMatch(string sentKey, string configuredKey) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sentKey), Encoding.UTF8.GetBytes(configuredKey));

    /// <summary>Writes the 401 in the usual error format; setting a Result stops the request before the action.</summary>
    private static async Task RejectAsync(AuthorizationFilterContext context)
    {
        await ProxyErrorWriter.WriteAsync(context.HttpContext.Response, ProxyErrorCatalog.AdminUnauthorized);
        context.Result = new EmptyResult();
    }
}
