namespace SecureProxy.Api.Forwarding;

/// <summary>
/// The result of sending a request to the destination.
///
/// When Outcome is Responded, Response holds the destination's answer. Only its headers have been
/// read; the body is still open and is streamed to the caller later. That is why this result must
/// be disposed once the response has been written.
/// </summary>
public sealed class ForwardResult : IDisposable
{
    private readonly CancellationTokenSource? _timeoutSource;

    private ForwardResult(ForwardOutcome outcome, HttpResponseMessage? response, CancellationTokenSource? timeoutSource)
    {
        Outcome = outcome;
        Response = response;
        _timeoutSource = timeoutSource;
    }

    public ForwardOutcome Outcome { get; }

    /// <summary>The destination's response. Set only when Outcome is Responded.</summary>
    public HttpResponseMessage? Response { get; }

    /// <summary>
    /// Cancelled when the caller disconnects or the destination's timeout runs out.
    /// Use it while reading the response body, so a destination that stalls mid-body is also cut off.
    /// </summary>
    public CancellationToken BodyCancellationToken => _timeoutSource?.Token ?? CancellationToken.None;

    public static ForwardResult Responded(HttpResponseMessage response, CancellationTokenSource timeoutSource) =>
        new(ForwardOutcome.Responded, response, timeoutSource);

    public static ForwardResult Failed(ForwardOutcome outcome) => new(outcome, null, null);

    public void Dispose()
    {
        Response?.Dispose();
        _timeoutSource?.Dispose();
    }
}
