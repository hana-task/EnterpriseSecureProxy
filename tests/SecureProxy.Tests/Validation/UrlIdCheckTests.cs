using SecureProxy.Api.Validation;

namespace SecureProxy.Tests.Validation;

public class UrlIdCheckTests
{
    [Fact]
    public void Matching_urlId_passes_and_is_removed_from_the_remaining_fields()
    {
        var envelope = TestData.Envelope("""{"urlId":"crm-customers","customerId":"123"}""");

        var result = UrlIdCheck.Check(envelope, TestData.CustomerGetPolicy());

        Assert.True(result.Passed);
        Assert.False(result.Value!.ContainsKey("urlId"));
        Assert.Equal("123", result.Value["customerId"].GetString());
    }

    [Theory]
    [InlineData("""{"customerId":"123"}""")]                                  // missing
    [InlineData("""{"urlId":"http://evil.example/steal"}""")]                  // a URL, not an id
    [InlineData("""{"urlId":"Crm-Customers"}""")]                              // wrong case
    [InlineData("""{"urlId":"other-operation"}""")]                            // another id
    [InlineData("""{"urlId":5}""")]                                            // not a string
    [InlineData("""{"urlId":null}""")]
    public void Missing_or_different_urlId_is_rejected(string json)
    {
        var result = UrlIdCheck.Check(TestData.Envelope(json), TestData.CustomerGetPolicy());

        Assert.Equal(RejectionReason.UrlIdMismatch, result.Rejection!.Reason);
    }
}
