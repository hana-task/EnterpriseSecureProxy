using Microsoft.AspNetCore.Mvc;
using SecureProxy.Api.Proxy;

namespace SecureProxy.Api.Controllers;

/// <summary>
/// The single public endpoint. It only hands the request to ProxyService, which writes the answer itself.
///
/// There is deliberately no model parameter ([FromBody]): the checks need the raw body (size, duplicate
/// fields, encrypted payload), and automatic model validation would answer with its own 400 format.
/// </summary>
[ApiController]
[Route("api/proxy")]
public sealed class ProxyController : ControllerBase
{
    private readonly ProxyService _proxyService;

    public ProxyController(ProxyService proxyService)
    {
        _proxyService = proxyService;
    }

    /// <summary>The response (destination answer or proxy error) is already written when this returns.</summary>
    [HttpPost("execute")]
    public Task Execute() => _proxyService.ExecuteAsync(HttpContext);
}
