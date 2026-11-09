using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SecureProxy.Api.Errors;

namespace SecureProxy.Tests.Errors;

public class ProxyErrorWriterTests
{
    private static readonly ProxyError SampleError =
        new(404, "unknown_operation", "Unknown operation", "The requested operation does not exist.");

    private static (DefaultHttpContext Context, MemoryStream Body) NewContext()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "trace-123" };
        var body = new MemoryStream();
        context.Response.Body = body;
        return (context, body);
    }

    private static JsonElement ReadJson(MemoryStream body) => JsonDocument.Parse(body.ToArray()).RootElement;

    [Fact]
    public async Task Writes_problem_details_with_error_code_and_correlation_id()
    {
        var (context, body) = NewContext();

        await ProxyErrorWriter.WriteAsync(context.Response, SampleError);

        Assert.Equal(404, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType);
        var json = ReadJson(body);
        Assert.Equal(404, json.GetProperty("status").GetInt32());
        Assert.Equal("Unknown operation", json.GetProperty("title").GetString());
        Assert.Equal("The requested operation does not exist.", json.GetProperty("detail").GetString());
        Assert.Equal("unknown_operation", json.GetProperty("errorCode").GetString());
        Assert.Equal("trace-123", json.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Marks_the_error_as_generated_by_the_proxy()
    {
        var (context, _) = NewContext();

        await ProxyErrorWriter.WriteAsync(context.Response, SampleError);

        Assert.Equal("proxy", context.Response.Headers["X-Proxy-Source"]);
    }

    [Fact]
    public async Task Headers_set_before_the_error_are_removed()
    {
        var (context, _) = NewContext();
        context.Response.StatusCode = 200;
        context.Response.Headers["X-Proxy-Source"] = "upstream";
        context.Response.Headers["ETag"] = "\"from-destination\"";

        await ProxyErrorWriter.WriteAsync(context.Response, SampleError);

        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal("proxy", context.Response.Headers["X-Proxy-Source"]);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
    }

    [Fact]
    public async Task Internal_error_reveals_nothing_about_the_failure()
    {
        var (context, body) = NewContext();

        await ProxyErrorWriter.WriteAsync(context.Response, ProxyErrorCatalog.InternalError);

        var json = ReadJson(body);
        Assert.Equal(500, json.GetProperty("status").GetInt32());
        Assert.Equal("internal_error", json.GetProperty("errorCode").GetString());
        Assert.Equal("An unexpected error occurred.", json.GetProperty("detail").GetString());
        Assert.False(json.TryGetProperty("exception", out _));
        Assert.False(json.TryGetProperty("stackTrace", out _));
    }
}
