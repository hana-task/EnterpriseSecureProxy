using System.Text;
using Microsoft.AspNetCore.Http;
using SecureProxy.Api.Validation;

namespace SecureProxy.Tests.Validation;

public class EnvelopeCheckTests
{
    private const int Limit = 1000;

    private static HttpRequest CreateRequest(string body, string? contentType = "application/json", long? declaredLength = null)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = contentType;
        context.Request.ContentLength = declaredLength ?? bytes.Length;
        return context.Request;
    }

    private static Task<CheckResult<RequestEnvelope>> Check(HttpRequest request) =>
        EnvelopeCheck.CheckAsync(request, Limit, CancellationToken.None);

    [Fact]
    public async Task Valid_envelope_passes_and_exposes_clientId_and_json()
    {
        var result = await Check(CreateRequest("""{"clientId":"customer.get","json":{"urlId":"crm-customers"}}"""));

        Assert.True(result.Passed);
        Assert.Equal("customer.get", result.Value!.ClientId);
        Assert.Equal("crm-customers", result.Value.Json.GetProperty("urlId").GetString());
    }

    [Fact]
    public async Task Content_type_with_charset_is_accepted()
    {
        var result = await Check(CreateRequest("""{"clientId":"a","json":{}}""", "application/json; charset=utf-8"));
        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    [InlineData(null)]
    public async Task Wrong_or_missing_content_type_is_rejected(string? contentType)
    {
        var result = await Check(CreateRequest("""{"clientId":"a","json":{}}""", contentType));
        Assert.Equal(RejectionReason.InvalidContentType, result.Rejection!.Reason);
    }

    [Fact]
    public async Task Declared_size_over_limit_is_rejected()
    {
        var result = await Check(CreateRequest("""{"clientId":"a","json":{}}""", declaredLength: Limit + 1));
        Assert.Equal(RejectionReason.RequestTooLarge, result.Rejection!.Reason);
    }

    [Fact]
    public async Task Actual_size_over_limit_is_rejected_even_when_no_length_is_declared()
    {
        var bigBody = "{\"clientId\":\"a\",\"json\":{\"x\":\"" + new string('a', Limit) + "\"}}";
        var request = CreateRequest(bigBody);
        request.ContentLength = null; // like a chunked upload

        var result = await Check(request);

        Assert.Equal(RejectionReason.RequestTooLarge, result.Rejection!.Reason);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("""{"clientId":"a","json":{},"clientId":"b"}""")]     // duplicate field
    [InlineData("""{"clientId":"a","json":{"urlId":"x","urlId":"y"}}""")] // duplicate inside json
    [InlineData("""{"clientId":"a","json":{},"url":"http://evil"}""")]  // unsupported top-level field
    [InlineData("""{"clientId":"a","json":{},"method":"DELETE"}""")]
    [InlineData("""{"json":{}}""")]                                      // no clientId
    [InlineData("""{"clientId":"","json":{}}""")]
    [InlineData("""{"clientId":5,"json":{}}""")]
    [InlineData("""{"clientId":"a"}""")]                                 // no json
    [InlineData("""{"clientId":"a","json":"text"}""")]
    [InlineData("""{"clientId":"a","json":[1]}""")]
    public async Task Malformed_envelope_is_rejected(string body)
    {
        var result = await Check(CreateRequest(body));
        Assert.Equal(RejectionReason.MalformedRequest, result.Rejection!.Reason);
    }

    // "\uD800" is valid JSON syntax but not a valid character; reading it used to throw (code review finding).
    [Theory]
    [InlineData("""{"clientId":"\uD800","json":{}}""")]
    [InlineData("""{"clientId":"a","json":{"name":"\uD800"}}""")]
    [InlineData("""{"clientId":"a","json":{"\uD800":"x"}}""")]
    [InlineData("""{"clientId":"a","json":{"list":["ok","\uD800"]}}""")]
    public async Task Invalid_text_is_rejected_instead_of_throwing(string body)
    {
        var result = await Check(CreateRequest(body));
        Assert.Equal(RejectionReason.MalformedRequest, result.Rejection!.Reason);
    }

    [Fact]
    public async Task Too_long_clientId_is_rejected()
    {
        var body = "{\"clientId\":\"" + new string('a', 101) + "\",\"json\":{}}";
        var result = await Check(CreateRequest(body));
        Assert.Equal(RejectionReason.MalformedRequest, result.Rejection!.Reason);
    }
}
