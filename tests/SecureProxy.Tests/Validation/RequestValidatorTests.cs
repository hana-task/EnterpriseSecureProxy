using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using SecureProxy.Api.Data.Entities;
using SecureProxy.Api.Policies;
using SecureProxy.Api.Security.Encryption;
using SecureProxy.Api.Forwarding;
using SecureProxy.Api.Validation;
using SecureProxy.Tests.Security.Encryption;

namespace SecureProxy.Tests.Validation;

public class RequestValidatorTests
{
    private const string PaymentKeyReference = "payments-v1";
    private static readonly byte[] PaymentKey = RandomNumberGenerator.GetBytes(32);

    private sealed class FakeKeySource : IKeySource
    {
        public byte[]? GetKey(string keyReference) => keyReference == PaymentKeyReference ? (byte[])PaymentKey.Clone() : null;
    }

    /// <summary>Serves fixed operations and counts how many times the configuration was read.</summary>
    private sealed class FakePolicyProvider : IPolicyProvider
    {
        public Dictionary<string, OperationPolicy> Operations { get; } = new();
        public int OperationLookups { get; private set; }

        public Task<OperationPolicy?> FindOperationAsync(string operationKey, CancellationToken cancellationToken)
        {
            OperationLookups++;
            return Task.FromResult(Operations.GetValueOrDefault(operationKey));
        }

        public Task<ProxySettings> GetSettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProxySettings { GlobalMaxRequestBytes = 65_536, ConfigurationCacheSeconds = 30 });

        public void InvalidateAll() { }
    }

    private readonly FakePolicyProvider _policies = new();
    private readonly RequestValidator _validator;

    public RequestValidatorTests()
    {
        var customerGet = TestData.CustomerGetPolicy() with
        {
            Parameters = new[]
            {
                new ParameterPolicy { Name = "customerId", Location = ParameterLocation.Path,
                    DataType = ParameterDataType.Integer, IsRequired = true, MinValue = 1 }
            },
            AllowedHeaders = new[] { "Accept-Language", "Authorization" } // Authorization must still be dropped
        };

        var paymentCreate = TestData.CustomerGetPolicy() with
        {
            Key = "payment.create", UrlId = "payments", HttpMethod = "POST", PathTemplate = "/payments",
            RequiresEncryption = true, EncryptionAlgorithm = "AES-256-GCM", EncryptionKeyReference = PaymentKeyReference,
            Parameters = new[]
            {
                new ParameterPolicy { Name = "accountId", Location = ParameterLocation.Body, DataType = ParameterDataType.Integer, IsRequired = true, MinValue = 1 },
                new ParameterPolicy { Name = "amount", Location = ParameterLocation.Body, DataType = ParameterDataType.Decimal, IsRequired = true, MinValue = 0.01m }
            }
        };

        _policies.Operations["customer.get"] = customerGet;
        _policies.Operations["payment.create"] = paymentCreate;
        _validator = new RequestValidator(_policies, new AesGcmPayloadDecryptor(new FakeKeySource()));
    }

    private static HttpRequest CreateRequest(string body, string contentType = "application/json")
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = contentType;
        context.Request.ContentLength = bytes.Length;
        return context.Request;
    }

    private Task<PreForwardResult> Validate(HttpRequest request) => _validator.ValidateAsync(request, CancellationToken.None);

    // ---- success ----

    [Fact]
    public async Task Valid_plain_request_produces_the_upstream_request_from_configuration()
    {
        var request = CreateRequest("""{"clientId":"customer.get","json":{"urlId":"crm-customers","customerId":"123"}}""");
        request.Headers["Accept-Language"] = "he";
        request.Headers["Authorization"] = "Bearer secret";
        request.Headers["User-Agent"] = "test";

        var result = await Validate(request);

        Assert.True(result.Passed);
        Assert.Equal("GET", result.Request!.HttpMethod);
        Assert.Equal("http://localhost:5101/customers/123", result.Request.Url.AbsoluteUri);
        Assert.Equal("Accept-Language", Assert.Single(result.Request.Headers).Key); // Authorization and User-Agent dropped
        Assert.Equal("customer.get", result.OperationKey);
    }

    [Fact]
    public async Task Valid_encrypted_request_is_decrypted_and_the_body_is_rebuilt()
    {
        var payload = AesGcmPayloadDecryptorTests.Encrypt("""{"accountId":7,"amount":10.5}""", PaymentKey, "payment.create");
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            clientId = "payment.create",
            json = new { urlId = "payments", nonce = payload.Nonce, ciphertext = payload.Ciphertext, tag = payload.Tag }
        });

        var result = await Validate(CreateRequest(body));

        Assert.True(result.Passed);
        Assert.Equal("POST", result.Request!.HttpMethod);
        Assert.Equal("""{"accountId":7,"amount":10.5}""", result.Request.BodyJson);
    }

    // ---- rejections ----

    [Fact]
    public async Task Unknown_operation_is_rejected_and_the_key_is_kept_for_audit()
    {
        var result = await Validate(CreateRequest("""{"clientId":"nope","json":{"urlId":"x"}}"""));

        Assert.Equal(RejectionReason.UnknownOperation, result.Rejection!.Reason);
        Assert.Equal("nope", result.OperationKey);
        Assert.Null(result.Operation);
        Assert.Null(result.Request);
    }

    [Fact]
    public async Task Disabled_operation_is_rejected_and_its_policy_is_kept_for_audit()
    {
        _policies.Operations["customer.get"] = _policies.Operations["customer.get"] with { IsEnabled = false };

        var result = await Validate(CreateRequest("""{"clientId":"customer.get","json":{"urlId":"crm-customers","customerId":"1"}}"""));

        Assert.Equal(RejectionReason.OperationDisabled, result.Rejection!.Reason);
        Assert.NotNull(result.Operation);
    }

    [Fact]
    public async Task Caller_cannot_choose_the_destination_in_any_field()
    {
        // As the urlId, as a parameter, or as a top-level field.
        var asUrlId = await Validate(CreateRequest("""{"clientId":"customer.get","json":{"urlId":"http://evil.example","customerId":"1"}}"""));
        var asParameter = await Validate(CreateRequest("""{"clientId":"customer.get","json":{"urlId":"crm-customers","customerId":"1","url":"http://evil.example"}}"""));
        var asTopLevel = await Validate(CreateRequest("""{"clientId":"customer.get","url":"http://evil.example","json":{"urlId":"crm-customers","customerId":"1"}}"""));

        Assert.Equal(RejectionReason.UrlIdMismatch, asUrlId.Rejection!.Reason);
        Assert.Equal(RejectionReason.UnknownParameter, asParameter.Rejection!.Reason);
        Assert.Equal(RejectionReason.MalformedRequest, asTopLevel.Rejection!.Reason);
    }

    [Fact]
    public async Task Path_traversal_in_a_parameter_is_rejected()
    {
        var result = await Validate(CreateRequest("""{"clientId":"customer.get","json":{"urlId":"crm-customers","customerId":"../admin"}}"""));

        Assert.Equal(RejectionReason.InvalidParameter, result.Rejection!.Reason);
        Assert.Null(result.Request);
    }

    [Fact]
    public async Task Plain_request_to_an_operation_that_requires_encryption_is_rejected()
    {
        var result = await Validate(CreateRequest("""{"clientId":"payment.create","json":{"urlId":"payments","accountId":7,"amount":10}}"""));

        Assert.Equal(RejectionReason.EncryptionRequired, result.Rejection!.Reason);
    }

    [Fact]
    public async Task Checks_stop_at_the_first_failure_without_reading_configuration()
    {
        var result = await Validate(CreateRequest("""{"clientId":"customer.get","json":{}}""", contentType: "text/plain"));

        Assert.Equal(RejectionReason.InvalidContentType, result.Rejection!.Reason);
        Assert.Equal(0, _policies.OperationLookups); // the operation was never even looked up
    }
}
