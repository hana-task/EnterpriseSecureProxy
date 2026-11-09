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
    private ForwardResult(ForwardOutcome outcome, HttpResponseMessage? response)
    {
        Outcome = outcome;
        Response = response;
    }

    public ForwardOutcome Outcome { get; }

    /// <summary>The destination's response. Set only when Outcome is Responded.</summary>
    public HttpResponseMessage? Response { get; }

    public static ForwardResult Responded(HttpResponseMessage response) => new(ForwardOutcome.Responded, response);

    public void Dispose() => Response?.Dispose();
}
