using System.Text.Json;
using SecureProxy.Api.Policies;

namespace SecureProxy.Api.Validation;

/// <summary>
/// Check 3: "json" must contain a urlId equal to the one configured for the operation.
/// urlId is only an identifier that has to match: the destination itself always comes from the
/// configuration, so a caller sending "http://evil.com" here is simply rejected.
///
/// On success returns the remaining fields of "json" (without urlId) for the next checks.
/// </summary>
public static class UrlIdCheck
{
    private const string UrlIdFieldName = "urlId";

    public static CheckResult<IReadOnlyDictionary<string, JsonElement>> Check(RequestEnvelope envelope, OperationPolicy policy)
    {
        var remainingFields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        JsonElement? urlId = null;

        foreach (var property in envelope.Json.EnumerateObject())
        {
            if (property.Name == UrlIdFieldName)
                urlId = property.Value;
            else
                remainingFields[property.Name] = property.Value;
        }

        // Exact, case-sensitive comparison.
        var matches = urlId is { ValueKind: JsonValueKind.String }
                      && string.Equals(urlId.Value.GetString(), policy.UrlId, StringComparison.Ordinal);

        return matches
            ? CheckResult<IReadOnlyDictionary<string, JsonElement>>.Pass(remainingFields)
            : CheckResult<IReadOnlyDictionary<string, JsonElement>>.Reject(
                RejectionReason.UrlIdMismatch, "'urlId' is missing or does not match the operation.");
    }
}
