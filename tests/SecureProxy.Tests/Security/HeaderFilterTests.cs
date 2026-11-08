using Microsoft.AspNetCore.Http;
using SecureProxy.Api.Security.Headers;

namespace SecureProxy.Tests.Security;

public class HeaderFilterTests
{
    private static HeaderDictionary Headers(params (string Name, string Value)[] headers)
    {
        var dictionary = new HeaderDictionary();
        foreach (var (name, value) in headers)
            dictionary[name] = value;
        return dictionary;
    }

    private static List<string> ForwardedNames(HeaderDictionary incoming, params string[] allowed) =>
        HeaderFilter.Filter(incoming, allowed).Select(header => header.Key).ToList();

    [Fact]
    public void Allowed_header_is_forwarded_with_its_value()
    {
        var result = HeaderFilter.Filter(Headers(("Accept-Language", "he-IL")), new[] { "Accept-Language" });

        var header = Assert.Single(result);
        Assert.Equal("Accept-Language", header.Key);
        Assert.Equal("he-IL", header.Value);
    }

    [Fact]
    public void Header_not_on_the_allowlist_is_not_forwarded()
    {
        var names = ForwardedNames(Headers(("Accept-Language", "he"), ("User-Agent", "x"), ("X-Custom", "y")), "Accept-Language");

        Assert.Equal(new[] { "Accept-Language" }, names);
    }

    [Fact]
    public void Nothing_is_forwarded_when_the_allowlist_is_empty()
    {
        Assert.Empty(ForwardedNames(Headers(("Accept-Language", "he"))));
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Cookie")]
    [InlineData("Proxy-Authorization")]
    [InlineData("X-Api-Key")]
    [InlineData("Host")]
    public void Sensitive_headers_are_not_forwarded_even_if_the_allowlist_contains_them(string name)
    {
        var names = ForwardedNames(Headers((name, "secret")), name);

        Assert.Empty(names);
    }

    [Theory]
    [InlineData("Transfer-Encoding")]
    [InlineData("Upgrade")]
    [InlineData("Keep-Alive")]
    [InlineData("TE")]
    [InlineData("Proxy-Connection")]
    [InlineData("Expect")]
    [InlineData("Content-Encoding")]   // describes the caller's body; the proxy sends its own body
    [InlineData("Content-Length")]
    [InlineData("Content-MD5")]
    public void Hop_by_hop_headers_are_not_forwarded_even_if_the_allowlist_contains_them(string name)
    {
        Assert.Empty(ForwardedNames(Headers((name, "x")), name));
    }

    [Fact]
    public void Header_named_in_the_Connection_header_is_not_forwarded()
    {
        var incoming = Headers(("Connection", "close, X-Custom"), ("X-Custom", "1"), ("Accept-Language", "he"));

        var names = ForwardedNames(incoming, "X-Custom", "Accept-Language");

        Assert.Equal(new[] { "Accept-Language" }, names);
    }

    [Fact]
    public void Header_names_are_compared_without_regard_to_case()
    {
        var names = ForwardedNames(Headers(("accept-language", "he"), ("AUTHORIZATION", "secret")), "ACCEPT-LANGUAGE", "authorization");

        Assert.Equal(new[] { "accept-language" }, names);
    }

    [Fact]
    public void Multiple_values_are_joined()
    {
        var incoming = new HeaderDictionary { ["Accept-Language"] = new[] { "he", "en" } };

        var result = HeaderFilter.Filter(incoming, new[] { "Accept-Language" });

        Assert.Equal("he, en", Assert.Single(result).Value);
    }

    [Fact]
    public void Value_with_a_line_break_is_not_forwarded()
    {
        var names = ForwardedNames(Headers(("Idempotency-Key", "abc\r\nX-Injected: 1")), "Idempotency-Key");

        Assert.Empty(names);
    }
}
