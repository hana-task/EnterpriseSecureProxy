using SecureProxy.Api.Forwarding;
using SecureProxy.Api.Policies;

namespace SecureProxy.Api.Validation;

/// <summary>
/// The outcome of all pre-forwarding checks: either a request ready to send (Request), or a Rejection.
/// OperationKey and Operation are filled as far as they were known, so the audit log can describe
/// a rejected request too.
/// </summary>
public sealed record PreForwardResult
{
    public string? OperationKey { get; init; }
    public OperationPolicy? Operation { get; init; }
    public UpstreamRequest? Request { get; init; }
    public Rejection? Rejection { get; init; }

    public bool Passed => Rejection is null;
}
