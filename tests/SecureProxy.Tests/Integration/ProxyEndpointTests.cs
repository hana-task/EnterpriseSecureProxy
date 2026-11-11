using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecureProxy.Api.Audit;
using SecureProxy.Api.Data;
using SecureProxy.Api.Data.Entities;
using SecureProxy.Api.Errors;
using SecureProxy.Api.Forwarding;
using SecureProxy.Api.Policies;
using SecureProxy.Tests.Forwarding;

namespace SecureProxy.Tests.Integration;

/// <summary>
/// The required scenarios (assignment section 9) through the whole application:
/// HTTP request → controller → checks with configuration from SQL Server → fake destination → HTTP answer.
/// The configuration is the seed data of the migration (see SeedData).
///
/// Every rejection test asserts that the destination received nothing.
/// The tests of one class run one after another, so sharing one fake destination is safe.
/// The admin API tests are in ProxyEndpointTests.Admin.cs: the same class (partial), so they share this
/// fixture and its database. A second test class would rebuild the same database while these tests run.
/// </summary>
public partial class ProxyEndpointTests : IClassFixture<ProxyApplicationFactory>
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

    // ---- 13. upstream timeout ----

    [Fact]
    public async Task Destination_that_does_not_answer_in_time_gets_a_504_from_the_proxy()
    {
        // The destination waits forever (until it is cancelled). test.slow.get has a 1-second timeout.
        _destination.Reset(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var response = await SendAsync("""{"clientId":"test.slow.get","json":{"urlId":"test-slow"}}""");

        // The request was valid, so it did reach the destination; the proxy stopped waiting.
        Assert.Single(_destination.Requests);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(ProxyErrorWriter.ProxySourceValue, SourceOf(response));
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("upstream_timeout", problem.GetProperty("errorCode").GetString());

        // The audit tells "forwarded, no answer" apart from a rejection: no upstream status, the proxy's 504.
        var record = await AuditRecordOfAsync(response);
        Assert.True(record.WasForwarded);
        Assert.Null(record.UpstreamStatusCode);
        Assert.Equal(504, record.ProxyStatusCode);
        Assert.Equal(RequestOutcome.UpstreamTimeout, record.Outcome);
        Assert.Equal("Timeout", record.Reason);
    }

    // ---- 14. upstream HTTP error ----

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Destination_error_status_is_passed_on_as_is_and_marked_as_upstream(HttpStatusCode statusCode)
    {
        _destination.Reset(statusCode, "destination error details");

        var response = await SendAsync(
            """{"clientId":"customer.get","json":{"urlId":"crm-customers","customerId":"123"}}""");

        // The proxy did its job; the error is the destination's own answer, not a proxy failure.
        Assert.Single(_destination.Requests);
        Assert.Equal(statusCode, response.StatusCode);
        Assert.Equal("destination error details", await response.Content.ReadAsStringAsync());
        Assert.Equal(UpstreamResponseWriter.SourceHeaderValue, SourceOf(response));
    }

    // ---- 15. cancellation is propagated ----

    [Fact]
    public async Task Caller_who_cancels_cancels_the_request_to_the_destination()
    {
        var destinationReached = new TaskCompletionSource();
        var destinationCancelled = new TaskCompletionSource();
        _destination.Reset(async (_, token) =>
        {
            destinationReached.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token); // wait until someone cancels
            }
            catch (OperationCanceledException)
            {
                destinationCancelled.SetResult(); // the caller's cancellation arrived here
                throw;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var callerCancellation = new CancellationTokenSource();
        var sending = _caller.PostAsync(Endpoint, new StringContent(
            """{"clientId":"customer.get","json":{"urlId":"crm-customers","customerId":"123"}}""",
            Encoding.UTF8, "application/json"), callerCancellation.Token);

        await destinationReached.Task.WaitAsync(TimeSpan.FromSeconds(5)); // the request is now at the destination
        callerCancellation.Cancel();                                       // the caller gives up

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        // Completes only if the cancellation travelled caller → proxy → destination.
        // (customer.get's own timeout is 10 seconds, so this is not the timeout firing.)
        await destinationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ---- unexpected exception (stage 6: the global handler, through the real middleware) ----

    private sealed class FailingPolicyProvider : IPolicyProvider
    {
        private static Exception Failure() =>
            new InvalidOperationException("Cannot open database on sql-prod-07 (internal detail)");

        public Task<OperationPolicy?> FindOperationAsync(string operationKey, CancellationToken cancellationToken) =>
            throw Failure();

        public Task<ProxySettings> GetSettingsAsync(CancellationToken cancellationToken) => throw Failure();

        public void InvalidateAll() { }
    }

    [Fact]
    public async Task Unexpected_exception_becomes_a_generic_500_without_internal_details()
    {
        // A second copy of the app in which reading configuration throws (like a database failure).
        using var failingApp = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddScoped<IPolicyProvider, FailingPolicyProvider>()));

        var response = await failingApp.CreateClient().PostAsync(Endpoint, new StringContent(
            """{"clientId":"customer.get","json":{"urlId":"crm-customers","customerId":"123"}}""",
            Encoding.UTF8, "application/json"));

        await AssertRejectedByProxyAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.DoesNotContain("sql-prod-07", await response.Content.ReadAsStringAsync());
    }

    // ---- the real network handler (what the RecordingHandler cannot show, because it replaces it) ----

    [Fact]
    public async Task Real_network_handler_adds_no_tracing_headers_and_follows_no_redirects()
    {
        // 1. A real HTTP server on a free local port. It records every request and answers with a redirect.
        var port = FreeLocalPort();
        var received = new List<(string Path, NameValueCollection Headers)>();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://localhost:{port}/");
        server.Start();
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                var exchange = await server.GetContextAsync();
                received.Add((exchange.Request.Url!.AbsolutePath, exchange.Request.Headers));
                exchange.Response.StatusCode = 302;
                exchange.Response.RedirectLocation = $"http://localhost:{port}/redirected";
                exchange.Response.Close();
            }
        });

        // 2. An operation whose destination is that server.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();
            db.Operations.Add(new Operation
            {
                Key = "test.real.get", UrlId = "test-real", HttpMethod = "GET", PathTemplate = "/start",
                IsEnabled = true, MaxRequestBytes = 4096, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                HeaderRules = { new HeaderRule { HeaderName = "Accept-Language" } },
                Destination = new Destination
                {
                    Name = "test-real", BaseUrl = $"http://localhost:{port}", IsEnabled = true, TimeoutSeconds = 5,
                    MaxResponseBytes = 1_048_576, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                }
            });
            await db.SaveChangesAsync();
        }

        // 3. A copy of the app with the production network handler put back in place of the RecordingHandler.
        using var realNetworkApp = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(UpstreamForwarder.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(UpstreamForwarder.CreatePrimaryHandler)));
        var caller = realNetworkApp.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent("""{"clientId":"test.real.get","json":{"urlId":"test-real"}}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Accept-Language", "he-IL");
        request.Headers.Add("traceparent", "00-11111111111111111111111111111111-2222222222222222-01");
        var response = await caller.SendAsync(request);

        // Exactly one request reached the server: the redirect to /redirected was not followed.
        var (path, headers) = Assert.Single(received);
        Assert.Equal("/start", path);

        // Only the allowed header; no tracing header (before the fix, the caller's trace id arrived here).
        Assert.Equal("he-IL", headers["Accept-Language"]);
        Assert.Null(headers["traceparent"]);

        // The caller gets the destination's 302, without its Location (an internal address).
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(UpstreamResponseWriter.SourceHeaderValue, SourceOf(response));
    }

    // ---- correlation id (stage 8) ----

    private static string CorrelationIdOf(HttpResponseMessage response) =>
        string.Join(",", response.Headers.GetValues(CorrelationIdMiddleware.HeaderName));

    [Fact]
    public async Task Proxy_error_carries_a_new_correlation_id_in_the_header_and_in_the_body()
    {
        var response = await SendAsync(
            """{"clientId":"customer.delete","json":{"urlId":"crm-customers","customerId":"123"}}""");

        // The caller sent no id, so the proxy created one (a Guid without dashes).
        var correlationId = CorrelationIdOf(response);
        Assert.Matches("^[0-9a-f]{32}$", correlationId);

        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(correlationId, problem.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Caller_correlation_id_is_kept_and_returned_with_the_destination_answer()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent("""{"clientId":"customer.get","json":{"urlId":"crm-customers","customerId":"123"}}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "caller-order-42");

        var response = await _caller.SendAsync(request);

        // The id is not forwarded: it is not in the operation's allowed headers.
        var received = Assert.Single(_destination.Requests);
        Assert.False(received.Headers.ContainsKey(CorrelationIdMiddleware.HeaderName));

        // The destination never saw the id, so the proxy is the one that put it on the answer (OnStarting).
        Assert.Equal(UpstreamResponseWriter.SourceHeaderValue, SourceOf(response));
        Assert.Equal("caller-order-42", CorrelationIdOf(response));
    }

    [Fact]
    public async Task Invalid_caller_correlation_id_is_replaced_by_a_new_one()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent("""{"clientId":"customer.delete","json":{"urlId":"crm-customers"}}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, "<script>alert(1)</script>");

        var response = await _caller.SendAsync(request);

        Assert.Matches("^[0-9a-f]{32}$", CorrelationIdOf(response));
    }

    // ---- 17. audit records for successful, rejected and failed requests (stage 8) ----

    /// <summary>
    /// The RequestLogs row of a request, found by the correlation id the caller got back.
    /// The body is read to the end first: the test server ends the body only when the whole pipeline has
    /// finished, so after this the final audit save (at the end of ProxyService) has happened.
    /// </summary>
    private async Task<RequestLog> AuditRecordOfAsync(HttpResponseMessage response)
    {
        await response.Content.ReadAsStringAsync();
        var correlationId = CorrelationIdOf(response);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();
        return await db.RequestLogs.AsNoTracking().SingleAsync(record => record.CorrelationId == correlationId);
    }

    [Fact]
    public async Task Successful_request_is_audited_as_forwarded_with_both_status_codes()
    {
        var response = await SendAsync(
            """{"clientId":"customer.get","json":{"urlId":"crm-customers","customerId":"123"}}""");

        Assert.Single(_destination.Requests);
        var record = await AuditRecordOfAsync(response);

        Assert.Equal("customer.get", record.OperationKey);
        Assert.Equal("crm", record.DestinationName);
        Assert.True(record.WasForwarded);
        Assert.Equal(200, record.UpstreamStatusCode);
        Assert.Equal(200, record.ProxyStatusCode);
        Assert.Equal(RequestOutcome.Success, record.Outcome); // not left InProgress: the end save updated the row
        Assert.Null(record.Reason);
        Assert.NotNull(record.CompletedAt);
        Assert.True(record.CompletedAt >= record.StartedAt);
        Assert.NotNull(record.DurationMs);
    }

    [Fact]
    public async Task Rejected_request_is_audited_as_not_forwarded_with_the_reason()
    {
        var response = await SendAsync(
            """{"clientId":"customer.export","json":{"urlId":"crm-customer-export"}}""");

        await AssertRejectedByProxyAsync(response, HttpStatusCode.Forbidden, "operation_disabled");
        var record = await AuditRecordOfAsync(response);

        Assert.Equal("customer.export", record.OperationKey);
        Assert.Equal("crm", record.DestinationName);
        Assert.False(record.WasForwarded);
        Assert.Null(record.UpstreamStatusCode);
        Assert.Equal(403, record.ProxyStatusCode);
        Assert.Equal(RequestOutcome.Rejected, record.Outcome);
        Assert.Equal("OperationDisabled", record.Reason);
        Assert.NotNull(record.CompletedAt);
    }

    [Fact]
    public async Task Upstream_error_is_audited_as_a_failed_request()
    {
        _destination.Reset(HttpStatusCode.InternalServerError, """{"error":"database down"}""");

        var response = await SendAsync(
            """{"clientId":"customer.get","json":{"urlId":"crm-customers","customerId":"123"}}""");

        Assert.Single(_destination.Requests);
        var record = await AuditRecordOfAsync(response);

        Assert.True(record.WasForwarded);
        Assert.Equal(500, record.UpstreamStatusCode);
        Assert.Equal(500, record.ProxyStatusCode);
        Assert.Equal(RequestOutcome.UpstreamError, record.Outcome);
    }

    [Fact]
    public async Task Unknown_operation_key_is_audited_with_unsafe_characters_replaced()
    {
        // Decision 14: the key is the caller's text, so a line break or markup must not reach the logs.
        var response = await SendAsync(
            """{"clientId":"fake\nline<b>","json":{"urlId":"crm-customers"}}""");

        await AssertRejectedByProxyAsync(response, HttpStatusCode.NotFound, "unknown_operation");
        var record = await AuditRecordOfAsync(response);

        Assert.Equal("fake?line?b?", record.OperationKey);
        Assert.Null(record.DestinationName);
        Assert.Equal("UnknownOperation", record.Reason);
    }

    // ---- SQL Server unavailable when the audit record must be written (stage 8, assignment section 8) ----

    /// <summary>
    /// Makes every save fail, as when SQL Server is down. Reads still work (configuration is read without
    /// SaveChanges), so only the audit write fails: exactly the case the audit policy is about.
    /// </summary>
    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated failure: SQL Server is unavailable");
    }

    /// <summary>A second copy of the app whose DbContext cannot save.</summary>
    private HttpClient CallerOfAppWithoutAuditStorage(out WebApplicationFactory<Program> app)
    {
        app = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ProxyDbContext>>();
            services.AddDbContext<ProxyDbContext>(options => options
                .UseSqlServer(_factory.ConnectionString)
                .AddInterceptors(new FailingSaveInterceptor()));
        }));
        return app.CreateClient();
    }

    private async Task<int> AuditRecordCountAsync(string correlationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();
        return await db.RequestLogs.CountAsync(record => record.CorrelationId == correlationId);
    }

    [Fact]
    public async Task Request_is_not_forwarded_when_its_audit_record_cannot_be_saved()
    {
        var caller = CallerOfAppWithoutAuditStorage(out var app);
        using var _ = app;

        var response = await caller.PostAsync(Endpoint, new StringContent(
            """{"clientId":"customer.get","json":{"urlId":"crm-customers","customerId":"123"}}""",
            Encoding.UTF8, "application/json"));

        // Mandatory audit before forwarding: no record, so the destination received nothing.
        await AssertRejectedByProxyAsync(response, HttpStatusCode.ServiceUnavailable, "audit_unavailable");
        Assert.Equal(0, await AuditRecordCountAsync(CorrelationIdOf(response)));
    }

    [Fact]
    public async Task Rejection_is_answered_as_usual_when_its_audit_record_cannot_be_saved()
    {
        var caller = CallerOfAppWithoutAuditStorage(out var app);
        using var _ = app;

        var response = await caller.PostAsync(Endpoint, new StringContent(
            """{"clientId":"customer.export","json":{"urlId":"crm-customer-export"}}""",
            Encoding.UTF8, "application/json"));

        // Best-effort audit for rejections: the caller gets the normal 403, not an audit error.
        // (The record goes to the technical log instead.)
        await AssertRejectedByProxyAsync(response, HttpStatusCode.Forbidden, "operation_disabled");
        Assert.Equal(0, await AuditRecordCountAsync(CorrelationIdOf(response)));
    }

    private static int FreeLocalPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
