using System.Net;
using SecureProxy.Api.Forwarding;
using SecureProxy.Tests.Validation;

namespace SecureProxy.Tests.Forwarding;

public class UpstreamForwarderTests
{
    private static readonly Uri CustomerUrl = new("http://localhost:5101/customers/123");

    private static UpstreamForwarder ForwarderFor(HttpMessageHandler handler) => new(new FakeHttpClientFactory(handler));

    // ---- sending ----

    [Fact]
    public async Task Request_is_sent_exactly_as_built()
    {
        var destination = new RecordingHandler(HttpStatusCode.OK);
        var request = new UpstreamRequest("POST", CustomerUrl, """{"name":"Dana"}""")
        {
            Headers = new[] { new KeyValuePair<string, string>("X-Request-Source", "portal") }
        };

        using var result = await ForwarderFor(destination).SendAsync(request, TestData.CustomerGetPolicy().Destination, CancellationToken.None);

        var received = Assert.Single(destination.Requests);
        Assert.Equal("POST", received.Method);
        Assert.Equal(CustomerUrl, received.Url);
        Assert.Equal("""{"name":"Dana"}""", received.Body);
        Assert.Equal("application/json", received.ContentType);
        Assert.Equal("portal", received.Headers["X-Request-Source"]);
    }

    [Fact]
    public async Task Request_without_body_is_sent_without_content()
    {
        var destination = new RecordingHandler(HttpStatusCode.OK);

        using var result = await ForwarderFor(destination).SendAsync(
            new UpstreamRequest("GET", CustomerUrl, null), TestData.CustomerGetPolicy().Destination, CancellationToken.None);

        Assert.Null(Assert.Single(destination.Requests).Body);
    }

    // ---- the destination's answer (assignment test 14: upstream HTTP error) ----

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Status_code_of_the_destination_is_preserved(HttpStatusCode statusCode)
    {
        var destination = new RecordingHandler(statusCode, """{"error":"from upstream"}""");

        using var result = await ForwarderFor(destination).SendAsync(
            new UpstreamRequest("GET", CustomerUrl, null), TestData.CustomerGetPolicy().Destination, CancellationToken.None);

        // An error status is still an answer from the destination, not a proxy failure.
        Assert.Equal(ForwardOutcome.Responded, result.Outcome);
        Assert.Equal(statusCode, result.Response!.StatusCode);
        Assert.Equal("""{"error":"from upstream"}""", await result.Response.Content.ReadAsStringAsync());
    }
}
