using System.Text.RegularExpressions;
using SecureProxy.Api.Data.Entities;
using SecureProxy.Api.Policies;
using SecureProxy.Api.Validation;

namespace SecureProxy.Api.Forwarding;

/// <summary>
/// Check 6: builds the request to the destination.
///
/// - Method, destination address and path template come from configuration only.
/// - Parameter values are placed into the path template and query string after being made safe.
/// - The final address must stay on the destination's scheme, host and port (and under its base path).
/// </summary>
public static partial class UpstreamRequestBuilder
{
    private static readonly string[] AllowedMethods = { "GET", "POST", "PUT", "PATCH", "DELETE" };

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")]
    private static partial Regex PlaceholderPattern();

    public static CheckResult<UpstreamRequest> Build(IReadOnlyList<ValidatedParameter> parameters, OperationPolicy policy)
    {
        // The database also restricts this, but the code does not trust that alone.
        if (!AllowedMethods.Contains(policy.HttpMethod, StringComparer.Ordinal))
            return Misconfigured<UpstreamRequest>();

        if (!Uri.TryCreate(policy.Destination.BaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
            return Misconfigured<UpstreamRequest>();

        // Must start with "/", otherwise text like "@other.com" could change the host.
        // Must not contain "?" or "#": the query string is built here, from the query parameters only.
        if (!policy.PathTemplate.StartsWith('/') || policy.PathTemplate.Contains('?') || policy.PathTemplate.Contains('#'))
            return Misconfigured<UpstreamRequest>();

        var path = FillPath(policy.PathTemplate, parameters);
        if (!path.Passed)
            return CheckResult<UpstreamRequest>.Reject(path.Rejection!.Reason, path.Rejection.SafeMessage);

        var finalText = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + path.Value + BuildQuery(parameters);
        if (!Uri.TryCreate(finalText, UriKind.Absolute, out var finalUrl) || !StaysOnDestination(baseUri, finalUrl))
            return Misconfigured<UpstreamRequest>();

        var bodyParameters = parameters.Where(parameter => parameter.Location == ParameterLocation.Body).ToList();
        if (bodyParameters.Count > 0 && policy.HttpMethod == "GET")
            return Misconfigured<UpstreamRequest>(); // a GET request has no body

        var bodyJson = bodyParameters.Count > 0 ? UpstreamBodyBuilder.Build(bodyParameters) : null;
        return CheckResult<UpstreamRequest>.Pass(new UpstreamRequest(policy.HttpMethod, finalUrl, bodyJson));
    }

    /// <summary>Replaces each {placeholder} in the template with its escaped, safe value.</summary>
    private static CheckResult<string> FillPath(string template, IReadOnlyList<ValidatedParameter> parameters)
    {
        var path = template;
        foreach (Match placeholder in PlaceholderPattern().Matches(template))
        {
            var name = placeholder.Groups[1].Value;
            var parameter = parameters.FirstOrDefault(
                candidate => candidate.Location == ParameterLocation.Path && candidate.Name == name);
            if (parameter is null)
                return Misconfigured<string>(); // the template needs a value that no rule provides

            if (!IsSafePathValue(parameter.CanonicalText))
                return CheckResult<string>.Reject(RejectionReason.InvalidPathValue,
                    $"Parameter '{name}' contains characters that are not allowed in a path.");

            // EscapeDataString also turns "?", "#", "@" and spaces into %-codes, so the value
            // can only ever be one path segment.
            path = path.Replace(placeholder.Value, Uri.EscapeDataString(parameter.CanonicalText));
        }

        // A leftover brace means a malformed template.
        return path.Contains('{') || path.Contains('}')
            ? Misconfigured<string>()
            : CheckResult<string>.Pass(path);
    }

    /// <summary>
    /// Path traversal protection: a value may not be "." or "..", may not contain a slash or
    /// backslash (it would start a new segment), a "%" (it could hide "%2e%2e" or "%2f"),
    /// or control characters.
    /// </summary>
    private static bool IsSafePathValue(string value) =>
        value.Length > 0
        && value is not ("." or "..")
        && value.All(character => character is not ('/' or '\\' or '%') && !char.IsControl(character));

    private static string BuildQuery(IReadOnlyList<ValidatedParameter> parameters)
    {
        var pairs = parameters
            .Where(parameter => parameter.Location == ParameterLocation.Query)
            .Select(parameter => Uri.EscapeDataString(parameter.Name) + "=" + Uri.EscapeDataString(parameter.CanonicalText))
            .ToList();

        return pairs.Count > 0 ? "?" + string.Join("&", pairs) : "";
    }

    /// <summary>
    /// The base path is compared with a trailing "/", so "/api" accepts "/api/x" but not "/api-admin/x"
    /// (a template with ".." could otherwise reach a sibling path, after Uri removes the "..").
    /// </summary>
    private static bool StaysOnDestination(Uri baseUri, Uri finalUrl) =>
        finalUrl.Scheme == baseUri.Scheme
        && string.Equals(finalUrl.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
        && finalUrl.Port == baseUri.Port
        && finalUrl.AbsolutePath.StartsWith(baseUri.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static CheckResult<T> Misconfigured<T>() =>
        CheckResult<T>.Reject(RejectionReason.ProxyMisconfigured, "The operation is not configured correctly.");
}
