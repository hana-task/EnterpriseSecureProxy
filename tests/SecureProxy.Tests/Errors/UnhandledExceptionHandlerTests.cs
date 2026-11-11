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

    [Theory]
    [InlineData(413, 413, "request_too_large")] // body over a size limit enforced by the server
    [InlineData(400, 400, "malformed_request")] // any other request the server could not read
    public async Task Request_the_server_refused_to_read_is_a_caller_error_not_a_500(
        int serverStatus, int expectedStatus, string expectedErrorCode)
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        var exception = new BadHttpRequestException("Request body too large. The max request body size is 4096 bytes.", serverStatus);

        await new UnhandledExceptionHandler().TryHandleAsync(context, exception, CancellationToken.None);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        var text = System.Text.Encoding.UTF8.GetString(body.ToArray());
        Assert.Equal(expectedErrorCode, JsonDocument.Parse(text).RootElement.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("4096", text); // the server's own message is not passed on
    }
}
