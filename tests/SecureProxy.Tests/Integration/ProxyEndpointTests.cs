using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureProxy.Api.Data;
using SecureProxy.Api.Data.Entities;
using SecureProxy.Api.Errors;
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

    private readonly ProxyApplicationFactory _factory;
    private readonly HttpClient _caller;
    private readonly RecordingHandler _destination;

    /// <summary>xUnit creates a new instance for every test, so this runs before each test.</summary>
    public ProxyEndpointTests(ProxyApplicationFactory factory)
    {
        _factory = factory;
        _destination = factory.Destination;
        _destination.Reset(HttpStatusCode.OK); // forget the previous test's requests
        _caller = factory.CreateClient();      // an HttpClient that sends to the in-memory server
    }

    private Task<HttpResponseMessage> SendAsync(string requestBody) =>
        _caller.PostAsync(Endpoint, new StringContent(requestBody, Encoding.UTF8, "application/json"));

    private static string SourceOf(HttpResponseMessage response) =>
        string.Join(",", response.Headers.GetValues(UpstreamResponseWriter.SourceHeaderName));

    /// <summary>
    /// The checks every rejection shares. The first one is the real proof that the request was blocked
    /// (the assignment: "a response code alone is not sufficient"); the others check the answer the caller got.
    /// </summary>
    private async Task AssertRejectedByProxyAsync(HttpResponseMessage response, HttpStatusCode statusCode, string errorCode)
    {
        Assert.Empty(_destination.Requests);

        Assert.Equal(statusCode, response.StatusCode);
        Assert.Equal(ProxyErrorWriter.ProxySourceValue, SourceOf(response));

        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(errorCode, problem.GetProperty("errorCode").GetString());
    }

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

    // ---- 2–4. unknown operation, disabled operation, disabled destination ----

    [Fact]
    public async Task Unknown_operation_is_rejected()
    {
        var response = await SendAsync(
            """{"clientId":"customer.delete","json":{"urlId":"crm-customers","customerId":"123"}}""");

        await AssertRejectedByProxyAsync(response, HttpStatusCode.NotFound, "unknown_operation");
    }

    [Fact]
    public async Task Disabled_operation_is_rejected()
    {
        // customer.export exists in the seed with IsEnabled = false.
        var response = await SendAsync(
            """{"clientId":"customer.export","json":{"urlId":"crm-customer-export"}}""");

        await AssertRejectedByProxyAsync(response, HttpStatusCode.Forbidden, "operation_disabled");
    }

    [Fact]
    public async Task Operation_on_a_disabled_destination_is_rejected()
    {
        // invoice.get is enabled, but its destination legacy-billing is disabled in the seed.
        var response = await SendAsync(
            """{"clientId":"invoice.get","json":{"urlId":"billing-invoices","invoiceId":"5"}}""");

        await AssertRejectedByProxyAsync(response, HttpStatusCode.ServiceUnavailable, "destination_unavailable");
    }

    // ---- 5. a destination URL from the caller cannot override configuration ----

    [Fact]
    public async Task Url_field_from_the_caller_is_rejected()
    {
        var response = await SendAsync(
            """{"clientId":"customer.get","json":{"urlId":"crm-customers","customerId":"123","url":"http://evil.example/steal"}}""");

        await AssertRejectedByProxyAsync(response, HttpStatusCode.BadRequest, "unknown_parameter");
    }

    [Fact]
    public async Task Raw_url_in_place_of_the_url_id_is_rejected()
    {
        var response = await SendAsync(
            """{"clientId":"customer.get","json":{"urlId":"http://evil.example/steal","customerId":"123"}}""");

        await AssertRejectedByProxyAsync(response, HttpStatusCode.BadRequest, "invalid_url_id");
    }

    [Fact]
    public async Task Url_field_is_ignored_by_an_operation_that_allows_unknown_parameters()
    {
        // customer.search allows unknown parameters, so the request passes, but the address still comes
        // only from configuration, and the unknown field is not sent on (decision 13).
        var response = await SendAsync(
            """{"clientId":"customer.search","json":{"urlId":"crm-customer-search","name":"Dana","url":"http://evil.example/steal"}}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = Assert.Single(_destination.Requests);
        Assert.Equal("http://localhost:5101/customers?name=Dana", received.Url.ToString());
    }

    // ---- 6. invalid or disallowed HTTP method: three layers ----

    [Fact]
    public async Task Method_field_from_the_caller_is_rejected()
    {
        // The method comes only from configuration; the caller cannot ask for another one.
        var response = await SendAsync(
            """{"clientId":"customer.get","json":{"urlId":"crm-customers","customerId":"123","method":"DELETE"}}""");

        await AssertRejectedByProxyAsync(response, HttpStatusCode.BadRequest, "unknown_parameter");
    }

    [Fact]
    public async Task Endpoint_accepts_only_post()
    {
        // ASP.NET answers 405 itself (its own format, decision 18), so only the status is checked here.
        var response = await _caller.GetAsync(Endpoint);

        Assert.Empty(_destination.Requests);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Operation_with_a_disallowed_method_cannot_be_configured()
    {
        // The database itself refuses any method outside GET/POST/PUT/PATCH/DELETE (CHECK constraint),
        // so such an operation can never be loaded. The code checks it again (UpstreamRequestBuilder).
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();
        db.Operations.Add(new Operation
        {
            Key = "test.trace", UrlId = "test-trace", DestinationId = 1, HttpMethod = "TRACE",
            PathTemplate = "/trace", IsEnabled = true, MaxRequestBytes = 4096,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("CK_Operations_HttpMethod", error.InnerException!.Message);
    }

    // ---- 7. missing required parameter ----

    [Fact]
    public async Task Missing_required_parameter_is_rejected()
    {
        var response = await SendAsync(
            """{"clientId":"customer.get","json":{"urlId":"crm-customers"}}""");

        await AssertRejectedByProxyAsync(response, HttpStatusCode.BadRequest, "missing_parameter");
    }

    // ---- 8. unknown parameters follow the operation's policy ----

    [Fact]
    public async Task Unknown_parameter_is_rejected_when_the_operation_does_not_allow_it()
    {
        var response = await SendAsync(
            """{"clientId":"customer.get","json":{"urlId":"crm-customers","customerId":"123","debug":"true"}}""");

        await AssertRejectedByProxyAsync(response, HttpStatusCode.BadRequest, "unknown_parameter");
    }

    [Fact]
    public async Task Unknown_parameter_is_accepted_but_not_forwarded_when_the_operation_allows_it()
    {
        var response = await SendAsync(
            """{"clientId":"customer.search","json":{"urlId":"crm-customer-search","name":"Dana","debug":"true"}}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = Assert.Single(_destination.Requests);
        Assert.Equal("http://localhost:5101/customers?name=Dana", received.Url.ToString());
    }
}
