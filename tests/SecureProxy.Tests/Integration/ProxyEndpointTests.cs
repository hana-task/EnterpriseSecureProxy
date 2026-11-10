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

    // ---- 9. path traversal ----

    [Theory]
    [InlineData("..")]
    [InlineData("../admin")]
    [InlineData("%2e%2e%2fadmin")]
    public async Task Path_traversal_in_a_path_parameter_is_rejected(string documentName)
    {
        var response = await SendAsync(JsonSerializer.Serialize(
            new { clientId = "test.document.get", json = new { urlId = "test-documents", documentName } }));

        await AssertRejectedByProxyAsync(response, HttpStatusCode.BadRequest, "invalid_path_value");
    }

    // ---- 10. disallowed headers are not forwarded ----

    [Fact]
    public async Task Only_allowed_headers_reach_the_destination()
    {
        // test.document.get allows Accept-Language and Authorization; Authorization is on the fixed forbidden list.
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(
                """{"clientId":"test.document.get","json":{"urlId":"test-documents","documentName":"report"}}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Accept-Language", "he-IL");          // allowed → forwarded
        request.Headers.Add("Authorization", "Bearer caller-secret"); // allowed in the DB, but forbidden in code
        request.Headers.Add("Cookie", "session=abc");              // not allowed, and forbidden
        request.Headers.Add("X-Forwarded-For", "10.0.0.1");        // not allowed
        request.Headers.Add("X-Custom", "anything");               // not allowed

        var response = await _caller.SendAsync(request);

        // The request is not rejected; the other headers are just dropped.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = Assert.Single(_destination.Requests);
        var header = Assert.Single(received.Headers);
        Assert.Equal("Accept-Language", header.Key);
        Assert.Equal("he-IL", header.Value);
    }

    // ---- 11–12. encrypted payloads ----

    /// <summary>
    /// Encrypts the parameters the way a caller would, and builds the request for payment.create.
    /// The associated data (AAD) is the operation key, as the proxy expects.
    /// </summary>
    private static (string Nonce, string Ciphertext, string Tag) EncryptPayment(
        string parametersJson, string operationKey = "payment.create")
    {
        var payload = Security.Encryption.AesGcmPayloadDecryptorTests.Encrypt(
            parametersJson, ProxyApplicationFactory.PaymentsKey, operationKey);
        return (payload.Nonce!, payload.Ciphertext!, payload.Tag!);
    }

    private static string PaymentRequest(string nonce, string ciphertext, string tag) =>
        JsonSerializer.Serialize(new { clientId = "payment.create", json = new { urlId = "payments", nonce, ciphertext, tag } });

    private const string PaymentParameters = """{"accountId":1001,"amount":25.50,"currency":"ILS"}""";

    [Fact]
    public async Task Valid_encrypted_request_is_decrypted_and_forwarded()
    {
        // The positive case first: it proves the key is set up, so the rejections below fail
        // because of the bad payload and not because no key was found.
        var (nonce, ciphertext, tag) = EncryptPayment(PaymentParameters);

        var response = await SendAsync(PaymentRequest(nonce, ciphertext, tag));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = Assert.Single(_destination.Requests);
        Assert.Equal("POST", received.Method);
        Assert.Equal("http://localhost:5102/payments", received.Url.ToString());

        // The destination gets the decrypted parameters as a plain JSON body, rebuilt by the proxy.
        var body = JsonDocument.Parse(received.Body!).RootElement;
        Assert.Equal(1001, body.GetProperty("accountId").GetInt32());
        Assert.Equal(25.50m, body.GetProperty("amount").GetDecimal());
        Assert.Equal("ILS", body.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Invalid_authentication_tag_is_rejected()
    {
        var (nonce, ciphertext, tag) = EncryptPayment(PaymentParameters);
        var tagBytes = Convert.FromBase64String(tag);
        tagBytes[0] ^= 0x01; // change one bit, as an attacker who altered the message would have to

        var response = await SendAsync(PaymentRequest(nonce, ciphertext, Convert.ToBase64String(tagBytes)));

        await AssertRejectedByProxyAsync(response, HttpStatusCode.BadRequest, "invalid_encrypted_payload");
    }

    [Fact]
    public async Task Payload_that_is_not_valid_base64_is_never_forwarded()
    {
        var (_, ciphertext, tag) = EncryptPayment(PaymentParameters);

        var response = await SendAsync(PaymentRequest("not base64!", ciphertext, tag));

        await AssertRejectedByProxyAsync(response, HttpStatusCode.BadRequest, "invalid_encrypted_payload");
    }

    [Fact]
    public async Task Payload_encrypted_for_another_operation_is_never_forwarded()
    {
        // Correct key, untouched data, but encrypted for customer.get: the AAD does not match.
        var (nonce, ciphertext, tag) = EncryptPayment(PaymentParameters, operationKey: "customer.get");

        var response = await SendAsync(PaymentRequest(nonce, ciphertext, tag));

        await AssertRejectedByProxyAsync(response, HttpStatusCode.BadRequest, "invalid_encrypted_payload");
    }

    [Fact]
    public async Task Plain_parameters_for_an_operation_that_requires_encryption_are_never_forwarded()
    {
        var response = await SendAsync(
            """{"clientId":"payment.create","json":{"urlId":"payments","accountId":1001,"amount":25.50,"currency":"ILS"}}""");

        await AssertRejectedByProxyAsync(response, HttpStatusCode.BadRequest, "encryption_required");
    }
}
