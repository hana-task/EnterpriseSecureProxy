using SecureProxy.Api.Forwarding;
using SecureProxy.Api.Validation;

namespace SecureProxy.Api.Errors;

/// <summary>
/// The single place that decides which HTTP status and error code each failure gets.
/// A test walks every enum value, so adding a reason without a mapping fails the build's tests.
/// </summary>
public static class ProxyErrorCatalog
{
    /// <summary>A rejection before forwarding. The rejection's own safe message becomes the detail.</summary>
    public static ProxyError ForRejection(Rejection rejection)
    {
        var (statusCode, errorCode, title) = rejection.Reason switch
        {
            RejectionReason.InvalidContentType => (415, "invalid_content_type", "Invalid content type"),
            RejectionReason.RequestTooLarge => (413, "request_too_large", "Request too large"),
            RejectionReason.MalformedRequest => (400, "malformed_request", "Malformed request"),
            RejectionReason.UnknownOperation => (404, "unknown_operation", "Unknown operation"),
            RejectionReason.OperationDisabled => (403, "operation_disabled", "Operation disabled"),
            RejectionReason.DestinationDisabled => (503, "destination_unavailable", "Destination unavailable"),
            RejectionReason.UrlIdMismatch => (400, "invalid_url_id", "Invalid urlId"),
            RejectionReason.EncryptionRequired => (400, "encryption_required", "Encryption required"),

            // One public code for both: the caller learns nothing about which part of the decryption failed.
            // The exact reason is still kept for the audit log.
            RejectionReason.InvalidEncryptedPayload => (400, "invalid_encrypted_payload", "Invalid encrypted payload"),
            RejectionReason.DecryptionFailed => (400, "invalid_encrypted_payload", "Invalid encrypted payload"),

            RejectionReason.MissingParameter => (400, "missing_parameter", "Missing parameter"),
            RejectionReason.InvalidParameter => (400, "invalid_parameter", "Invalid parameter"),
            RejectionReason.UnknownParameter => (400, "unknown_parameter", "Unknown parameter"),
            RejectionReason.InvalidPathValue => (400, "invalid_path_value", "Invalid path value"),

            // Our configuration is wrong, not the caller's request.
            RejectionReason.ProxyMisconfigured => (500, "proxy_misconfigured", "Proxy misconfigured"),

            _ => throw new ArgumentOutOfRangeException(nameof(rejection), rejection.Reason, "No error mapping for this reason.")
        };

        return new ProxyError(statusCode, errorCode, title, rejection.SafeMessage);
    }

    /// <summary>
    /// A failure while reaching the destination. Responded and CallerCancelled are not errors
    /// the proxy answers with (the first passes the destination's answer on, the second has nobody to answer).
    /// </summary>
    public static ProxyError ForForwardFailure(ForwardOutcome outcome) => outcome switch
    {
        ForwardOutcome.Timeout => new(504, "upstream_timeout", "Upstream timeout",
            "The destination did not respond in time."),
        ForwardOutcome.ConnectionFailed => new(502, "upstream_unreachable", "Upstream unreachable",
            "The destination could not be reached."),
        ForwardOutcome.ResponseTooLarge => new(502, "upstream_response_too_large", "Upstream response too large",
            "The destination's response is larger than allowed."),

        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "This outcome is not a proxy error.")
    };

    /// <summary>Anything unexpected. Deliberately says nothing about what went wrong.</summary>
    public static ProxyError InternalError { get; } =
        new(500, "internal_error", "Internal error", "An unexpected error occurred.");
}
