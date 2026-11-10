using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SecureProxy.Api.Audit;
using SecureProxy.Api.Data;
using SecureProxy.Api.Data.Entities;
using SecureProxy.Api.Forwarding;
using SecureProxy.Api.Policies;
using SecureProxy.Api.Proxy;
using SecureProxy.Api.Security.Encryption;
using SecureProxy.Api.Validation;
using SecureProxy.Tests.Forwarding;
using SecureProxy.Tests.Validation;
using Serilog.Extensions.Hosting;

namespace SecureProxy.Tests.Proxy;

/// <summary>
/// The whole flow with real checks, a real forwarder and a fake destination (RecordingHandler).
/// Every rejection test also asserts that the destination received nothing.
/// </summary>
public class ProxyServiceTests
{
    private const string ValidBody =
        """{"clientId":"customer.get","json":{"urlId":"crm-customers","customerId":"123"}}""";

    private sealed class FakePolicyProvider : IPolicyProvider
    {
        public OperationPolicy Operation { get; set; } = TestData.CustomerGetPolicy() with
        {
            Parameters = new[]
            {
                new ParameterPolicy { Name = "customerId", Location = ParameterLocation.Path,
                    DataType = ParameterDataType.Integer, IsRequired = true, MinValue = 1 }
            }
        };

        public Task<OperationPolicy?> FindOperationAsync(string operationKey, CancellationToken cancellationToken) =>
            Task.FromResult(operationKey == Operation.Key ? Operation : null);

        public Task<ProxySettings> GetSettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProxySettings { GlobalMaxRequestBytes = 65_536, ConfigurationCacheSeconds = 30 });

        public void InvalidateAll() { }
    }

    private sealed class NoKeys : IKeySource
    {
        public byte[]? GetKey(string keyReference) => null;
    }

    private readonly FakePolicyProvider _policies = new();

    private ProxyService CreateService(RecordingHandler destination) =>
        new(new RequestValidator(_policies, new AesGcmPayloadDecryptor(new NoKeys())),
            new UpstreamForwarder(new FakeHttpClientFactory(destination), NullLogger<UpstreamForwarder>.Instance),
            CreateAuditor());

    /// <summary>
    /// An auditor whose saves always succeed: an in-memory database instead of SQL Server, so these tests
    /// stay unit tests. The audit records themselves are checked in the integration tests, against SQL Server.
    /// </summary>
    private static RequestAuditor CreateAuditor()
    {
        var options = new DbContextOptionsBuilder<ProxyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new RequestAuditor(new ProxyDbContext(options), new DiagnosticContext(null),
            NullLogger<RequestAuditor>.Instance);
    }

    private static (DefaultHttpContext Context, MemoryStream ResponseBody) NewContext(string requestBody)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(requestBody);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        return (context, responseBody);
    }

    private static JsonElement ReadJson(MemoryStream body) => JsonDocument.Parse(body.ToArray()).RootElement;

    // ---- success and destination answers ----

    [Fact]
    public async Task Valid_request_is_forwarded_and_the_destination_answer_is_returned()
    {
        var destination = new RecordingHandler(HttpStatusCode.OK, """{"name":"Dana"}""");
        var (context, body) = NewContext(ValidBody);

        await CreateService(destination).ExecuteAsync(context);

        var sent = Assert.Single(destination.Requests);
        Assert.Equal("http://localhost:5101/customers/123", sent.Url.ToString());
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("upstream", context.Response.Headers["X-Proxy-Source"]);
        Assert.Equal("""{"name":"Dana"}""", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task Destination_error_status_is_passed_on_as_is_and_marked_as_upstream()
    {
        var destination = new RecordingHandler(HttpStatusCode.InternalServerError, "destination failed");
        var (context, body) = NewContext(ValidBody);

        await CreateService(destination).ExecuteAsync(context);

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal("upstream", context.Response.Headers["X-Proxy-Source"]);
        Assert.Equal("destination failed", Encoding.UTF8.GetString(body.ToArray()));
    }

    // ---- rejections: proxy error, nothing sent ----

    [Fact]
    public async Task Rejected_request_gets_a_proxy_error_and_never_reaches_the_destination()
    {
        var destination = new RecordingHandler(HttpStatusCode.OK);
        var (context, body) = NewContext(ValidBody.Replace("customer.get", "customer.delete"));

        await CreateService(destination).ExecuteAsync(context);

        Assert.Empty(destination.Requests);
        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal("proxy", context.Response.Headers["X-Proxy-Source"]);
        Assert.Equal("unknown_operation", ReadJson(body).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Disabled_operation_is_rejected_and_never_reaches_the_destination()
    {
        _policies.Operation = _policies.Operation with { IsEnabled = false };
        var destination = new RecordingHandler(HttpStatusCode.OK);
        var (context, _) = NewContext(ValidBody);

        await CreateService(destination).ExecuteAsync(context);

        Assert.Empty(destination.Requests);
        Assert.Equal(403, context.Response.StatusCode);
    }

    // ---- forwarding failures ----

    [Fact]
    public async Task Destination_timeout_becomes_504_from_the_proxy()
    {
        _policies.Operation = _policies.Operation with
        {
            Destination = _policies.Operation.Destination with { TimeoutSeconds = 1 }
        };
        var destination = new RecordingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var (context, body) = NewContext(ValidBody);

        await CreateService(destination).ExecuteAsync(context);

        Assert.Equal(504, context.Response.StatusCode);
        Assert.Equal("proxy", context.Response.Headers["X-Proxy-Source"]);
        Assert.Equal("upstream_timeout", ReadJson(body).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Connection_failure_becomes_502_from_the_proxy()
    {
        var destination = new RecordingHandler((_, _) => throw new HttpRequestException("connection refused"));
        var (context, body) = NewContext(ValidBody);

        await CreateService(destination).ExecuteAsync(context);

        Assert.Equal(502, context.Response.StatusCode);
        Assert.Equal("upstream_unreachable", ReadJson(body).GetProperty("errorCode").GetString());
        Assert.DoesNotContain("connection refused", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task Response_too_large_before_anything_was_sent_becomes_502()
    {
        _policies.Operation = _policies.Operation with
        {
            Destination = _policies.Operation.Destination with { MaxResponseBytes = 5 }
        };
        var destination = new RecordingHandler(HttpStatusCode.OK, "much more than five bytes");
        var (context, body) = NewContext(ValidBody);

        await CreateService(destination).ExecuteAsync(context);

        Assert.Equal(502, context.Response.StatusCode);
        Assert.Equal("proxy", context.Response.Headers["X-Proxy-Source"]);
        Assert.Equal("upstream_response_too_large", ReadJson(body).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Failure_after_the_response_started_writes_no_error_and_aborts_the_connection()
    {
        _policies.Operation = _policies.Operation with
        {
            Destination = _policies.Operation.Destination with { MaxResponseBytes = 10_000 }
        };
        // No announced size, so the limit is only noticed while the body is already being sent.
        var destination = new RecordingHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(new byte[30_000]) }));
        var (context, body) = NewContext(ValidBody);
        var lifetime = new AbortRecorder();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        context.Features.Set<IHttpResponseFeature>(new AlreadyStartedResponseFeature());
        context.Response.Body = body;

        await CreateService(destination).ExecuteAsync(context);

        Assert.True(lifetime.WasAborted);
        // Only destination bytes (zeros) went out; no ProblemDetails was appended to the started response.
        Assert.True(body.Length <= 10_000);
        Assert.All(body.ToArray(), value => Assert.Equal(0, value));
    }

    // ---- caller cancellation ----

    [Fact]
    public async Task Caller_who_leaves_while_waiting_gets_nothing_written()
    {
        using var callerSource = new CancellationTokenSource();
        var destination = new RecordingHandler(async (_, token) =>
        {
            callerSource.Cancel(); // the caller disconnects while the destination is working
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var (context, body) = NewContext(ValidBody);
        context.RequestAborted = callerSource.Token;

        await CreateService(destination).ExecuteAsync(context);

        Assert.Equal(200, context.Response.StatusCode); // untouched default
        Assert.Equal(0, body.Length);
        Assert.False(context.Response.Headers.ContainsKey("X-Proxy-Source"));
    }

    [Fact]
    public async Task Caller_who_left_before_the_checks_gets_nothing_and_nothing_is_sent()
    {
        var destination = new RecordingHandler(HttpStatusCode.OK);
        var (context, body) = NewContext(ValidBody);
        context.RequestAborted = new CancellationToken(canceled: true);

        await CreateService(destination).ExecuteAsync(context);

        Assert.Empty(destination.Requests);
        Assert.Equal(0, body.Length);
    }

    private sealed class AbortRecorder : IHttpRequestLifetimeFeature
    {
        public bool WasAborted { get; private set; }
        public CancellationToken RequestAborted { get; set; }
        public void Abort() => WasAborted = true;
    }

    private sealed class AlreadyStartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}
