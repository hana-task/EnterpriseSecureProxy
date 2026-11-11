using Microsoft.AspNetCore.Mvc;
using SecureProxy.Api.Admin;
using Microsoft.AspNetCore.RateLimiting;
using SecureProxy.Api.Errors;
using SecureProxy.Api.Resilience;

namespace SecureProxy.Api.Controllers;

/// <summary>
/// Admin API for operation settings: read the current values and version, and update them.
/// Every action is protected by AdminKeyFilter (X-Admin-Key header).
///
/// Deliberately without [ApiController]: its automatic 400 has a different format (no errorCode, no correlationId)
/// and can describe JSON conversion details. Here every error goes through ProxyErrorWriter, like the proxy's.
/// </summary>
[Route("api/admin/operations")]
[ServiceFilter(typeof(AdminKeyFilter))]
[EnableRateLimiting(RateLimitingSetup.AdminPolicy)] // runs before the key check, so wrong keys count too
public sealed class AdminOperationsController : ControllerBase
{
    /// <summary>The update body is three small fields; anything larger is not read (no unbounded buffering).</summary>
    private const int MaxBodyBytes = 4096;

    private readonly OperationAdminService _adminService;

    public AdminOperationsController(OperationAdminService adminService)
    {
        _adminService = adminService;
    }

    /// <summary>Returns the settings and the rowVersion to send back with an update.</summary>
    [HttpGet("{key}")]
    public async Task<IActionResult> Get(string key)
    {
        var settings = await _adminService.GetAsync(key, HttpContext.RequestAborted);
        return settings is null ? await ErrorAsync(ProxyErrorCatalog.AdminUnknownOperation) : Ok(settings);
    }

    /// <summary>200 with the new values and new rowVersion; 409 when the sent rowVersion is no longer current.</summary>
    [HttpPut("{key}")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Update(string key, [FromBody] OperationUpdateRequest? request)
    {
        // Invalid JSON or a wrong type leaves request null / ModelState invalid; our own message, no conversion details.
        var problem = !ModelState.IsValid || request is null ? "The body must be a JSON object with only isEnabled (true/false), maxRequestBytes (number) and rowVersion (base64)." : request.FindProblem();
        if (problem is not null)
            return await ErrorAsync(ProxyErrorCatalog.InvalidAdminRequest(problem));

        var result = await _adminService.UpdateAsync(key, request!, HttpContext.RequestAborted);
        return result.Status switch
        {
            OperationUpdateStatus.Updated => Ok(result.Settings),
            OperationUpdateStatus.NotFound => await ErrorAsync(ProxyErrorCatalog.AdminUnknownOperation),
            _ => await ErrorAsync(ProxyErrorCatalog.ConcurrencyConflict)
        };
    }

    /// <summary>Writes the error in the usual format; EmptyResult then adds nothing to the written response.</summary>
    private async Task<IActionResult> ErrorAsync(ProxyError error)
    {
        await ProxyErrorWriter.WriteAsync(Response, error);
        return new EmptyResult();
    }
}
