using SecureProxy.Api.Validation;

namespace SecureProxy.Tests.Validation;

public class OperationCheckTests
{
    private static readonly RequestEnvelope SmallRequest = TestData.Envelope("{}");

    [Fact]
    public void Enabled_operation_on_enabled_destination_passes()
    {
        var result = OperationCheck.Check(TestData.CustomerGetPolicy(), SmallRequest);

        Assert.True(result.Passed);
        Assert.Equal("customer.get", result.Value!.Key);
    }

    [Fact]
    public void Unknown_operation_is_rejected()
    {
        var result = OperationCheck.Check(null, SmallRequest);
        Assert.Equal(RejectionReason.UnknownOperation, result.Rejection!.Reason);
    }

    [Fact]
    public void Disabled_operation_is_rejected()
    {
        var policy = TestData.CustomerGetPolicy() with { IsEnabled = false };

        var result = OperationCheck.Check(policy, SmallRequest);

        Assert.Equal(RejectionReason.OperationDisabled, result.Rejection!.Reason);
    }

    [Fact]
    public void Disabled_destination_is_rejected_even_when_the_operation_is_enabled()
    {
        var policy = TestData.CustomerGetPolicy();
        policy = policy with { Destination = policy.Destination with { IsEnabled = false } };

        var result = OperationCheck.Check(policy, SmallRequest);

        Assert.Equal(RejectionReason.DestinationDisabled, result.Rejection!.Reason);
    }

    [Fact]
    public void Request_larger_than_the_operation_limit_is_rejected()
    {
        var policy = TestData.CustomerGetPolicy() with { MaxRequestBytes = 100 };

        var result = OperationCheck.Check(policy, TestData.Envelope("{}", bodySizeBytes: 101));

        Assert.Equal(RejectionReason.RequestTooLarge, result.Rejection!.Reason);
    }
}
