using System.Net;
using System.Text;
using SecureProxy.Api.Forwarding;
using SecureProxy.Tests.Forwarding;

namespace SecureProxy.Tests.Integration;

/// <summary>
/// The required scenarios (assignment section 12) through the whole application:
/// HTTP request → controller → checks with configuration from SQL Server → fake destination → HTTP answer.
/// The configuration is the seed data of the migration (see SeedData).
///
/// Every rejection test asserts that the destination received nothing.
/// The tests of one class run one after another, so sharing one fake destination is safe.
/// </summary>
public class ProxyEndpointTests : IClassFixture<ProxyApplicationFactory>
{
    private const string Endpoint = "/api/proxy/execute";

    private readonly HttpClient _caller;
    private readonly RecordingHandler _destination;

    /// <summary>xUnit creates a new instance for every test, so this runs before each test.</summary>
    public ProxyEndpointTests(ProxyApplicationFactory factory)
    {
        _destination = factory.Destination;
        _destination.Reset(HttpStatusCode.OK); // forget the previous test's requests
        _caller = factory.CreateClient();      // an HttpClient that sends to the in-memory server
    }

    private Task<HttpResponseMessage> SendAsync(string requestBody) =>
        _caller.PostAsync(Endpoint, new StringContent(requestBody, Encoding.UTF8, "application/json"));

    private static string SourceOf(HttpResponseMessage response) =>
        string.Join(",", response.Headers.GetValues(UpstreamResponseWriter.SourceHeaderName));

    // ---- 1. valid operation is forwarded ----

    [Fact]
    public async Task Valid_operation_is_forwarded_and_the_destination_answer_is_returned()
    {
        _destination.Reset(HttpStatusCode.OK, """{"id":123,"name":"Dana"}""");

        var response = await SendAsync(
            """{"clientId":"customer.get","json":{"urlId":"crm-customers","customerId":"123"}}""");

        // What reached the destination: method and address come only from the configuration.
        var received = Assert.Single(_destination.Requests);
        Assert.Equal("GET", received.Method);
        Assert.Equal("http://localhost:5101/customers/123", received.Url.ToString());
        Assert.Null(received.Body);

        // What the caller got: the destination's own status and body, marked as coming from the destination.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"id":123,"name":"Dana"}""", await response.Content.ReadAsStringAsync());
        Assert.Equal(UpstreamResponseWriter.SourceHeaderValue, SourceOf(response));
    }
}
