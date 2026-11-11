using System.Text.Json.Serialization;

namespace SecureProxy.Api.Admin;

/// <summary>
/// What the admin API shows of an operation: the fields that may be changed, plus the version (rowversion)
/// the caller must send back with an update. byte[] is written to JSON as base64, e.g. "AAAAAAAAB9E=".
/// </summary>
public sealed record OperationSettings(string Key, bool IsEnabled, int MaxRequestBytes, byte[] RowVersion);

/// <summary>
/// The body of PUT /api/admin/operations/{key}. All fields are required; nullable only so a missing field
/// can be told apart from false / 0. Only these two fields can be changed through the API: destination,
/// method and path decide where requests go, so they are changed by migration or SQL, not by an API call.
/// Any other field rejects the request (JsonUnmappedMemberHandling.Disallow), instead of being silently ignored:
/// an admin who sends "httpMethod" must not get 200 and believe the method changed.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperationUpdateRequest(bool? IsEnabled, int? MaxRequestBytes, byte[]? RowVersion)
{
    /// <summary>The same upper bound as the global request size setting (10 MB).</summary>
    public const int LargestMaxRequestBytes = 10_485_760;

    /// <summary>SQL Server rowversion is always 8 bytes.</summary>
    private const int RowVersionLength = 8;

    /// <summary>Returns a safe message describing the first problem, or null when the request is valid.</summary>
    public string? FindProblem()
    {
        if (IsEnabled is null)
            return "isEnabled is required.";
        if (MaxRequestBytes is null or < 1 or > LargestMaxRequestBytes)
            return $"maxRequestBytes is required and must be between 1 and {LargestMaxRequestBytes}.";
        if (RowVersion is null || RowVersion.Length != RowVersionLength)
            return "rowVersion is required: send the value returned by GET, unchanged.";
        return null;
    }
}
