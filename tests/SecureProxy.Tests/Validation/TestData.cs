using System.Text.Json;
using SecureProxy.Api.Policies;
using SecureProxy.Api.Validation;

namespace SecureProxy.Tests.Validation;

/// <summary>Builders shared by the validation tests.</summary>
internal static class TestData
{
    /// <summary>Like the seeded customer.get operation. Change what you need with "with".</summary>
    public static OperationPolicy CustomerGetPolicy() => new()
    {
        Key = "customer.get",
        UrlId = "crm-customers",
        IsEnabled = true,
        HttpMethod = "GET",
        PathTemplate = "/customers/{customerId}",
        MaxRequestBytes = 4096,
        RequiresEncryption = false,
        AllowUnknownParameters = false,
        Destination = new DestinationPolicy
        {
            Name = "crm",
            BaseUrl = "http://localhost:5101",
            IsEnabled = true,
            TimeoutSeconds = 10,
            MaxResponseBytes = 1_048_576
        },
        Parameters = Array.Empty<ParameterPolicy>(),
        AllowedHeaders = Array.Empty<string>()
    };

    public static RequestEnvelope Envelope(string jsonObject, int bodySizeBytes = 100)
    {
        using var document = JsonDocument.Parse(jsonObject);
        return new RequestEnvelope("customer.get", document.RootElement.Clone(), bodySizeBytes);
    }
}
