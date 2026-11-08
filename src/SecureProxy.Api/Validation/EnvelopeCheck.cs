using System.Text.Json;
using Microsoft.Net.Http.Headers;

namespace SecureProxy.Api.Validation;

/// <summary>
/// Check 1: is this request shaped like {"clientId": "...", "json": {...}} and small enough?
/// Runs before the operation is known, so it uses only the global size limit.
/// </summary>
public static class EnvelopeCheck
{
    private const int MaxClientIdLength = 100;

    public static async Task<CheckResult<RequestEnvelope>> CheckAsync(
        HttpRequest request, int globalMaxRequestBytes, CancellationToken cancellationToken)
    {
        if (!IsJsonContentType(request.ContentType))
            return Reject(RejectionReason.InvalidContentType, "Content-Type must be application/json.");

        // Cheap check first: the declared size. Then read at most the limit (+1 byte to detect
        // overflow), because the declared size can be missing (chunked) or wrong.
        if (request.ContentLength > globalMaxRequestBytes)
            return Reject(RejectionReason.RequestTooLarge, "The request is too large.");

        var body = await ReadBodyAsync(request.Body, globalMaxRequestBytes, cancellationToken);
        if (body is null)
            return Reject(RejectionReason.RequestTooLarge, "The request is too large.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Reject(RejectionReason.MalformedRequest, "The request body is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            // Also reads all text once, so nothing below (or in later checks) can throw on invalid text.
            if (!JsonObjectHelper.IsValidObject(root))
                return Reject(RejectionReason.MalformedRequest, "The request must be a valid JSON object with unique fields.");

            // Only these two fields exist. Anything else (e.g. "url", "method") is rejected,
            // so the caller cannot try to smuggle in a destination.
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name is not ("clientId" or "json"))
                    return Reject(RejectionReason.MalformedRequest, "The request contains an unsupported field.");
            }

            if (!root.TryGetProperty("clientId", out var clientIdElement)
                || clientIdElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(clientIdElement.GetString())
                || clientIdElement.GetString()!.Length > MaxClientIdLength)
                return Reject(RejectionReason.MalformedRequest, "'clientId' is missing or invalid.");

            if (!root.TryGetProperty("json", out var jsonElement)
                || !JsonObjectHelper.IsValidObject(jsonElement))
                return Reject(RejectionReason.MalformedRequest, "'json' must be an object with unique fields.");

            // Clone: the element belongs to the document, which is disposed when we leave.
            return CheckResult<RequestEnvelope>.Pass(
                new RequestEnvelope(clientIdElement.GetString()!, jsonElement.Clone(), body.Length));
        }
    }

    private static bool IsJsonContentType(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && string.Equals(parsed.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the body, or returns null if it is larger than the limit.</summary>
    private static async Task<byte[]?> ReadBodyAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
                return null;
        }
        return buffer.ToArray();
    }

    private static CheckResult<RequestEnvelope> Reject(RejectionReason reason, string message) =>
        CheckResult<RequestEnvelope>.Reject(reason, message);
}
