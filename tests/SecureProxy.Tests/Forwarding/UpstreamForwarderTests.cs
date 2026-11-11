using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SecureProxy.Api.Forwarding;
using SecureProxy.Tests.Validation;

namespace SecureProxy.Tests.Forwarding;

public class UpstreamForwarderTests
{
    private static readonly Uri CustomerUrl = new("http://localhost:5101/customers/123");

    private static UpstreamForwarder ForwarderFor(HttpMessageHandler handler) =>
        new(new FakeHttpClientFactory(handler), NullLogger<UpstreamForwarder>.Instance);

    // ---- sending ----

    [Fact]
    public async Task Request_is_sent_exactly_as_built()
    {
        var destination = new RecordingHandler(HttpStatusCode.OK);
        var request = new UpstreamRequest("POST", CustomerUrl, """{"name":"Dana"}""")
        {
            Headers = new[] { new KeyValuePair<string, string>("X-Request-Source", "portal") }
        };

        using var result = await ForwarderFor(destination).SendAsync(request, TestData.CustomerGetPolicy().Destination, CancellationToken.None);

        var received = Assert.Single(destination.Requests);
        Assert.Equal("POST", received.Method);
        Assert.Equal(CustomerUrl, received.Url);
        Assert.Equal("""{"name":"Dana"}""", received.Body);
        Assert.Equal("application/json", received.ContentType);
        Assert.Equal("portal", received.Headers["X-Request-Source"]);
    }

    [Fact]
    public async Task Request_without_body_is_sent_without_content()
    {
        var destination = new RecordingHandler(HttpStatusCode.OK);

        using var result = await ForwarderFor(destination).SendAsync(
            new UpstreamRequest("GET", CustomerUrl, null), TestData.CustomerGetPolicy().Destination, CancellationToken.None);

        Assert.Null(Assert.Single(destination.Requests).Body);
    }

    // ---- the destination's answer (assignment test 14: upstream HTTP error) ----

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Status_code_of_the_destination_is_preserved(HttpStatusCode statusCode)
    {
        var destination = new RecordingHandler(statusCode, """{"error":"from upstream"}""");

        using var result = await ForwarderFor(destination).SendAsync(
            new UpstreamRequest("GET", CustomerUrl, null), TestData.CustomerGetPolicy().Destination, CancellationToken.None);

        // An error status is still an answer from the destination, not a proxy failure.
        Assert.Equal(ForwardOutcome.Responded, result.Outcome);
        Assert.Equal(statusCode, result.Response!.StatusCode);
        Assert.Equal("""{"error":"from upstream"}""", await result.Response.Content.ReadAsStringAsync());
    }

    // ---- timeout and cancellation (assignment tests 13 and 15) ----

    /// <summary>A destination that never answers, but notices when the request is cancelled.</summary>
    private static RecordingHandler NeverAnswers(Action onCancelled) => new(async (_, token) =>
    {
        try
        {
            await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException)
        {
            onCancelled();
            throw;
        }
        return new HttpResponseMessage(HttpStatusCode.OK);
    });

    [Fact]
    public async Task Destination_that_does_not_answer_in_time_is_a_timeout()
    {
        var destination = NeverAnswers(() => { });
        var oneSecondDestination = TestData.CustomerGetPolicy().Destination with { TimeoutSeconds = 1 };

        using var result = await ForwarderFor(destination).SendAsync(
            new UpstreamRequest("GET", CustomerUrl, null), oneSecondDestination, CancellationToken.None);

        Assert.Equal(ForwardOutcome.Timeout, result.Outcome);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task Caller_cancelling_is_not_reported_as_a_timeout_and_reaches_the_destination_call()
    {
        var cancellationReachedDestination = false;
        var destination = NeverAnswers(() => cancellationReachedDestination = true);
        using var callerSource = new CancellationTokenSource();

        var sending = ForwarderFor(destination).SendAsync(
            new UpstreamRequest("GET", CustomerUrl, null), TestData.CustomerGetPolicy().Destination, callerSource.Token);
        callerSource.Cancel();
        using var result = await sending;

        Assert.Equal(ForwardOutcome.CallerCancelled, result.Outcome);
        Assert.True(cancellationReachedDestination, "the caller's cancellation must reach the outgoing call");
    }

    [Fact]
    public async Task Failure_to_connect_is_reported_as_connection_failed()
    {
        var destination = new RecordingHandler((_, _) => throw new HttpRequestException("connection refused"));

        using var result = await ForwarderFor(destination).SendAsync(
            new UpstreamRequest("GET", CustomerUrl, null), TestData.CustomerGetPolicy().Destination, CancellationToken.None);

        Assert.Equal(ForwardOutcome.ConnectionFailed, result.Outcome);
        Assert.Null(result.Response);
    }

    // ---- retries (one retry, GET only, connection failures only) ----

    /// <summary>Fails to connect the given number of times, then answers 200.</summary>
    private static RecordingHandler FailsThenAnswers(int failures)
    {
        var calls = 0;
        return new RecordingHandler((_, _) => ++calls <= failures
            ? throw new HttpRequestException("connection refused")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
    }

    [Fact]
    public async Task Get_is_retried_once_after_a_connection_failure()
    {
        var destination = FailsThenAnswers(failures: 1);

        using var result = await ForwarderFor(destination).SendAsync(
            new UpstreamRequest("GET", CustomerUrl, null), TestData.CustomerGetPolicy().Destination, CancellationToken.None);

        Assert.Equal(ForwardOutcome.Responded, result.Outcome);
        Assert.Equal(2, destination.Requests.Count);
    }

    [Fact]
    public async Task Get_is_not_retried_more_than_once()
    {
        var destination = FailsThenAnswers(failures: 5);

        using var result = await ForwarderFor(destination).SendAsync(
            new UpstreamRequest("GET", CustomerUrl, null), TestData.CustomerGetPolicy().Destination, CancellationToken.None);

        Assert.Equal(ForwardOutcome.ConnectionFailed, result.Outcome);
        Assert.Equal(2, destination.Requests.Count);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Methods_that_change_data_are_never_retried(string method)
    {
        var destination = FailsThenAnswers(failures: 1);

        using var result = await ForwarderFor(destination).SendAsync(
            new UpstreamRequest(method, CustomerUrl, null), TestData.CustomerGetPolicy().Destination, CancellationToken.None);

        Assert.Equal(ForwardOutcome.ConnectionFailed, result.Outcome);
        Assert.Single(destination.Requests);
    }

    [Fact]
    public async Task Get_is_not_retried_after_an_http_error_status()
    {
        var destination = new RecordingHandler(HttpStatusCode.ServiceUnavailable);

        using var result = await ForwarderFor(destination).SendAsync(
            new UpstreamRequest("GET", CustomerUrl, null), TestData.CustomerGetPolicy().Destination, CancellationToken.None);

        Assert.Equal(ForwardOutcome.Responded, result.Outcome);
        Assert.Single(destination.Requests);
    }

    [Fact]
    public async Task Get_is_not_retried_after_a_timeout()
    {
        var destination = NeverAnswers(() => { });
        var oneSecondDestination = TestData.CustomerGetPolicy().Destination with { TimeoutSeconds = 1 };

        using var result = await ForwarderFor(destination).SendAsync(
            new UpstreamRequest("GET", CustomerUrl, null), oneSecondDestination, CancellationToken.None);

        Assert.Equal(ForwardOutcome.Timeout, result.Outcome);
        Assert.Single(destination.Requests);
    }

    [Fact]
    public async Task Unexpected_exceptions_are_not_hidden()
    {
        var destination = new RecordingHandler((_, _) => throw new InvalidOperationException("bug"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ForwarderFor(destination).SendAsync(
            new UpstreamRequest("GET", CustomerUrl, null), TestData.CustomerGetPolicy().Destination, CancellationToken.None));
    }
}
