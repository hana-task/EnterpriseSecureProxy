using System.Globalization;
using System.Text.Json;
using SecureProxy.Api.Data.Entities;
using SecureProxy.Api.Policies;

namespace SecureProxy.Api.Validation;

/// <summary>
/// Check 5: every parameter must follow the rules configured for the operation.
///
/// - Each configured parameter: present if required, and of the right type, length and range.
/// - A parameter that has no rule is "unknown": rejected, unless the operation allows unknown
///   parameters. Even when allowed, an unknown parameter is not forwarded; only what was checked is.
/// </summary>
public static class ParameterCheck
{
    public static CheckResult<IReadOnlyList<ValidatedParameter>> Check(
        IReadOnlyDictionary<string, JsonElement> fields, OperationPolicy policy)
    {
        var validated = new List<ValidatedParameter>();

        foreach (var rule in policy.Parameters)
        {
            if (!fields.TryGetValue(rule.Name, out var value))
            {
                // A path parameter is always needed: without it the path cannot be built.
                var mustBePresent = rule.IsRequired || rule.Location == ParameterLocation.Path;
                if (mustBePresent)
                    return Reject(RejectionReason.MissingParameter, $"Parameter '{rule.Name}' is required.");
                continue;
            }

            var canonicalText = ValidateValue(rule, value);
            if (canonicalText is null)
                return Reject(RejectionReason.InvalidParameter, $"Parameter '{rule.Name}' is invalid.");

            validated.Add(new ValidatedParameter(rule.Name, rule.Location, rule.DataType, canonicalText));
        }

        if (!policy.AllowUnknownParameters && fields.Keys.Any(name => policy.Parameters.All(rule => rule.Name != name)))
            return Reject(RejectionReason.UnknownParameter, "The request contains a parameter this operation does not accept.");

        return CheckResult<IReadOnlyList<ValidatedParameter>>.Pass(validated);
    }

    /// <summary>Returns the value in canonical form, or null if it breaks the rule.</summary>
    private static string? ValidateValue(ParameterPolicy rule, JsonElement value) => rule.DataType switch
    {
        ParameterDataType.String => ValidateString(rule, value),
        ParameterDataType.Integer => ValidateInteger(rule, value),
        ParameterDataType.Decimal => ValidateDecimal(rule, value),
        ParameterDataType.Boolean => ValidateBoolean(value),
        _ => null
    };

    private static string? ValidateString(ParameterPolicy rule, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            return null;

        var text = value.GetString()!;
        return rule.MaxLength is null || text.Length <= rule.MaxLength ? text : null;
    }

    /// <summary>Accepts a JSON integer (123) or a string of digits ("123"), nothing looser.</summary>
    private static string? ValidateInteger(ParameterPolicy rule, JsonElement value)
    {
        long number;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetInt64(out number))
                return null;
        }
        else if (value.ValueKind != JsonValueKind.String
                 || !long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number))
        {
            return null;
        }

        return IsInRange(number, rule) ? number.ToString(CultureInfo.InvariantCulture) : null;
    }

    private static string? ValidateDecimal(ParameterPolicy rule, JsonElement value)
    {
        decimal number;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDecimal(out number))
                return null;
        }
        else if (value.ValueKind != JsonValueKind.String
                 || !decimal.TryParse(value.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                     CultureInfo.InvariantCulture, out number))
        {
            return null;
        }

        return IsInRange(number, rule) ? number.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Only the JSON values true and false. The text "true" is not accepted.</summary>
    private static string? ValidateBoolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };

    private static bool IsInRange(decimal number, ParameterPolicy rule) =>
        (rule.MinValue is null || number >= rule.MinValue) && (rule.MaxValue is null || number <= rule.MaxValue);

    private static CheckResult<IReadOnlyList<ValidatedParameter>> Reject(RejectionReason reason, string message) =>
        CheckResult<IReadOnlyList<ValidatedParameter>>.Reject(reason, message);
}
