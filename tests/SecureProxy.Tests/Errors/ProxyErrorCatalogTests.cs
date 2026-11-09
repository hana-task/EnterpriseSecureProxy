using SecureProxy.Api.Errors;
using SecureProxy.Api.Forwarding;
using SecureProxy.Api.Validation;

namespace SecureProxy.Tests.Errors;

public class ProxyErrorCatalogTests
{
    public static TheoryData<RejectionReason> AllRejectionReasons()
    {
        var data = new TheoryData<RejectionReason>();
        foreach (var reason in Enum.GetValues<RejectionReason>())
            data.Add(reason);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllRejectionReasons))]
    public void Every_rejection_reason_has_a_mapping(RejectionReason reason)
    {
        var error = ProxyErrorCatalog.ForRejection(new Rejection(reason, "safe message"));

        Assert.InRange(error.StatusCode, 400, 599);
        Assert.False(string.IsNullOrWhiteSpace(error.ErrorCode));
        Assert.Equal("safe message", error.Detail);
    }

    [Theory]
    [InlineData(RejectionReason.InvalidContentType, 415)]
    [InlineData(RejectionReason.RequestTooLarge, 413)]
    [InlineData(RejectionReason.UnknownOperation, 404)]
    [InlineData(RejectionReason.OperationDisabled, 403)]
    [InlineData(RejectionReason.DestinationDisabled, 503)]
    [InlineData(RejectionReason.MissingParameter, 400)]
    [InlineData(RejectionReason.ProxyMisconfigured, 500)]
    public void Rejection_gets_the_documented_status(RejectionReason reason, int expectedStatus)
    {
        Assert.Equal(expectedStatus, ProxyErrorCatalog.ForRejection(new Rejection(reason, "m")).StatusCode);
    }

    [Fact]
    public void Both_decryption_failures_share_one_public_error_code()
    {
        var invalidPayload = ProxyErrorCatalog.ForRejection(new Rejection(RejectionReason.InvalidEncryptedPayload, "m"));
        var decryptionFailed = ProxyErrorCatalog.ForRejection(new Rejection(RejectionReason.DecryptionFailed, "m"));

        Assert.Equal(invalidPayload.ErrorCode, decryptionFailed.ErrorCode);
        Assert.Equal(invalidPayload.StatusCode, decryptionFailed.StatusCode);
    }

    [Theory]
    [InlineData(ForwardOutcome.Timeout, 504)]
    [InlineData(ForwardOutcome.ConnectionFailed, 502)]
    [InlineData(ForwardOutcome.ResponseTooLarge, 502)]
    public void Forward_failure_gets_the_documented_status(ForwardOutcome outcome, int expectedStatus)
    {
        Assert.Equal(expectedStatus, ProxyErrorCatalog.ForForwardFailure(outcome).StatusCode);
    }

    [Theory]
    [InlineData(ForwardOutcome.Responded)]
    [InlineData(ForwardOutcome.CallerCancelled)]
    public void Outcomes_that_are_not_proxy_errors_have_no_mapping(ForwardOutcome outcome)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProxyErrorCatalog.ForForwardFailure(outcome));
    }

    [Fact]
    public void Every_forward_outcome_is_either_mapped_or_explicitly_not_an_error()
    {
        foreach (var outcome in Enum.GetValues<ForwardOutcome>())
        {
            if (outcome is ForwardOutcome.Responded or ForwardOutcome.CallerCancelled)
                continue;
            Assert.InRange(ProxyErrorCatalog.ForForwardFailure(outcome).StatusCode, 500, 599);
        }
    }
}
