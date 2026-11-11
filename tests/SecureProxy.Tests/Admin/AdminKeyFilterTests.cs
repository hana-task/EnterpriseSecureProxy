using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SecureProxy.Api.Admin;

namespace SecureProxy.Tests.Admin;

public class AdminKeyFilterTests
{
    private const string ConfiguredKey = "test-admin-key-123";

    private static AdminKeyFilter FilterWithKey(string? configuredKey)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AdminKeyFilter.ConfigurationKey] = configuredKey })
            .Build();
        return new AdminKeyFilter(configuration, NullLogger<AdminKeyFilter>.Instance);
    }

    /// <summary>The context ASP.NET gives an authorization filter, for a request with the given header value.</summary>
    private static (AuthorizationFilterContext Context, MemoryStream Body) RequestWithKey(string? sentKey)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        if (sentKey is not null)
            httpContext.Request.Headers[AdminKeyFilter.HeaderName] = sentKey;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return (new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>()),
            (MemoryStream)httpContext.Response.Body);
    }

    private static async Task AssertRejectedAsync(AdminKeyFilter filter, string? sentKey)
    {
        var (context, body) = RequestWithKey(sentKey);

        await filter.OnAuthorizationAsync(context);

        Assert.IsType<EmptyResult>(context.Result); // a Result stops the request before the action runs
        Assert.Equal(401, context.HttpContext.Response.StatusCode);
        var problem = JsonDocument.Parse(body.ToArray()).RootElement;
        Assert.Equal("admin_unauthorized", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Correct_key_lets_the_request_through()
    {
        var (context, _) = RequestWithKey(ConfiguredKey);

        await FilterWithKey(ConfiguredKey).OnAuthorizationAsync(context);

        Assert.Null(context.Result); // no Result = the action runs
    }

    [Fact]
    public Task Missing_key_is_rejected() => AssertRejectedAsync(FilterWithKey(ConfiguredKey), sentKey: null);

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("test-admin-key-12")]   // a prefix of the right key
    [InlineData("TEST-ADMIN-KEY-123")]  // the comparison is case-sensitive
    [InlineData("")]
    public Task Wrong_key_is_rejected(string sentKey) => AssertRejectedAsync(FilterWithKey(ConfiguredKey), sentKey);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public Task Every_request_is_rejected_when_no_key_is_configured(string? configuredKey) =>
        AssertRejectedAsync(FilterWithKey(configuredKey), sentKey: "");
}
