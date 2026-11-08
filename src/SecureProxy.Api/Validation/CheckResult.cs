namespace SecureProxy.Api.Validation;

/// <summary>A rejection: the reason, and a short message that is safe to show the caller.</summary>
public sealed record Rejection(RejectionReason Reason, string SafeMessage);

/// <summary>
/// What a single check returns: either "passed" with a value for the next check,
/// or "rejected" with a reason. Checks never throw for a bad request.
/// </summary>
public sealed class CheckResult<T>
{
    private CheckResult(T? value, Rejection? rejection)
    {
        Value = value;
        Rejection = rejection;
    }

    public T? Value { get; }
    public Rejection? Rejection { get; }
    public bool Passed => Rejection is null;

    public static CheckResult<T> Pass(T value) => new(value, null);

    /// <summary>The message must never contain values sent by the caller.</summary>
    public static CheckResult<T> Reject(RejectionReason reason, string safeMessage) =>
        new(default, new Rejection(reason, safeMessage));
}
