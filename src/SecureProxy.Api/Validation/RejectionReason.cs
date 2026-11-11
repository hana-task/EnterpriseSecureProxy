namespace SecureProxy.Api.Validation;

/// <summary>
/// Why a request was rejected before forwarding. ProxyErrorCatalog maps each reason to an HTTP response;
/// RequestAuditor writes it to the audit log.
/// </summary>
public enum RejectionReason
{
    InvalidContentType,
    RequestTooLarge,
    MalformedRequest,
    UnknownOperation,
    OperationDisabled,
    DestinationDisabled,
    UrlIdMismatch,
    EncryptionRequired,
    InvalidEncryptedPayload,
    DecryptionFailed,
    MissingParameter,
    InvalidParameter,
    UnknownParameter,
    InvalidPathValue,

    /// <summary>The stored configuration itself is wrong (not the caller's fault).</summary>
    ProxyMisconfigured
}
