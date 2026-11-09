using SecureProxy.Api.Policies;

namespace SecureProxy.Api.Validation;

/// <summary>
/// Check 2: may this operation run at all? The operation must exist, be enabled, point to an
/// enabled destination, and the request must fit the operation's own size limit.
/// The policy is looked up by the caller of this check (null = no such operation).
/// </summary>
public static class OperationCheck
{
    // Same range as the CK_Destinations_TimeoutSeconds constraint in the database.
    private const int MinTimeoutSeconds = 1;
    private const int MaxTimeoutSeconds = 300;

    public static CheckResult<OperationPolicy> Check(OperationPolicy? policy, RequestEnvelope envelope)
    {
        if (policy is null)
            return Reject(RejectionReason.UnknownOperation, "The requested operation does not exist.");

        if (!policy.IsEnabled)
            return Reject(RejectionReason.OperationDisabled, "The requested operation is disabled.");

        if (!policy.Destination.IsEnabled)
            return Reject(RejectionReason.DestinationDisabled, "The destination of this operation is disabled.");

        // The database restricts these too (CHECK constraints), but they are re-checked before anything
        // is sent: a bad timeout would throw inside the forwarder, and a non-positive response limit would
        // only be noticed after the destination had already done the work (for example a POST).
        if (policy.Destination.TimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds
            || policy.Destination.MaxResponseBytes <= 0)
            return Reject(RejectionReason.ProxyMisconfigured, "The operation is not configured correctly.");

        if (envelope.BodySizeBytes > policy.MaxRequestBytes)
            return Reject(RejectionReason.RequestTooLarge, "The request is too large for this operation.");

        return CheckResult<OperationPolicy>.Pass(policy);
    }

    private static CheckResult<OperationPolicy> Reject(RejectionReason reason, string message) =>
        CheckResult<OperationPolicy>.Reject(reason, message);
}
