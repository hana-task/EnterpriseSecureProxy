using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SecureProxy.Api.Errors;

namespace SecureProxy.Tests.Errors;

public class UnhandledExceptionHandlerTests
{
    [Fact]
    public async Task Unexpected_exception_becomes_a_generic_500_without_internal_details()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        var exception = new InvalidOperationException("Login failed for user 'sa' on server sql-internal-01");

        var handled = await new UnhandledExceptionHandler().TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal("proxy", context.Response.Headers["X-Proxy-Source"]);
        var text = System.Text.Encoding.UTF8.GetString(body.ToArray());
        Assert.Equal("internal_error", JsonDocument.Parse(text).RootElement.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("sql-internal-01", text);
        Assert.DoesNotContain(nameof(InvalidOperationException), text);
    }
}
