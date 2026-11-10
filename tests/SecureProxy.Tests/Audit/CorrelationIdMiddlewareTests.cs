using SecureProxy.Api.Audit;

namespace SecureProxy.Tests.Audit;

/// <summary>Which caller-supplied correlation ids are accepted. Anything else is replaced by a new id.</summary>
public class CorrelationIdMiddlewareTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("order-42_v1.0")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123")] // exactly 64
    public void Short_ids_with_safe_characters_are_accepted(string value) =>
        Assert.True(CorrelationIdMiddleware.IsValid(value));

    [Theory]
    [InlineData("")]                                                                  // missing header
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")] // 65: longer than the column
    [InlineData("a b")]                                                               // space
    [InlineData("a\r\nfake log line")]                                                // log injection
    [InlineData("id-1,id-2")]                                                         // two header values joined
    [InlineData("מזהה")]                                                              // non-ASCII letters
    public void Empty_long_or_unsafe_ids_are_rejected(string value) =>
        Assert.False(CorrelationIdMiddleware.IsValid(value));
}
