using System.Text.Json;
using SecureProxy.Api.Data.Entities;
using SecureProxy.Api.Policies;
using SecureProxy.Api.Forwarding;
using SecureProxy.Api.Validation;
using SecureProxy.Tests.Validation;

namespace SecureProxy.Tests.Forwarding;

public class UpstreamRequestBuilderTests
{
    private static ValidatedParameter PathParameter(string name, string value, ParameterDataType type = ParameterDataType.String) =>
        new(name, ParameterLocation.Path, type, value);

    private static ValidatedParameter QueryParameter(string name, string value, ParameterDataType type = ParameterDataType.String) =>
        new(name, ParameterLocation.Query, type, value);

    private static ValidatedParameter BodyParameter(string name, string value, ParameterDataType type) =>
        new(name, ParameterLocation.Body, type, value);

    private static OperationPolicy PolicyWith(string template, string method = "GET", string baseUrl = "http://localhost:5101") =>
        TestData.CustomerGetPolicy() with
        {
            PathTemplate = template,
            HttpMethod = method,
            Destination = TestData.CustomerGetPolicy().Destination with { BaseUrl = baseUrl }
        };

    // ---- address ----

    [Fact]
    public void Path_value_is_placed_into_the_template_on_the_configured_destination()
    {
        var parameters = new[] { PathParameter("customerId", "123", ParameterDataType.Integer) };

        var result = UpstreamRequestBuilder.Build(parameters, PolicyWith("/customers/{customerId}"));

        Assert.True(result.Passed);
        Assert.Equal("GET", result.Value!.HttpMethod);
        Assert.Equal("http://localhost:5101/customers/123", result.Value.Url.AbsoluteUri);
        Assert.Null(result.Value.BodyJson);
    }

    [Fact]
    public void Query_values_are_encoded()
    {
        var parameters = new[]
        {
            QueryParameter("name", "a&b=c d"),
            QueryParameter("page", "2", ParameterDataType.Integer)
        };

        var result = UpstreamRequestBuilder.Build(parameters, PolicyWith("/customers"));

        Assert.Equal("http://localhost:5101/customers?name=a%26b%3Dc%20d&page=2", result.Value!.Url.AbsoluteUri);
    }

    [Fact]
    public void Hebrew_text_is_encoded_in_the_query()
    {
        var result = UpstreamRequestBuilder.Build(new[] { QueryParameter("name", "שלום") }, PolicyWith("/customers"));

        Assert.Equal("http://localhost:5101/customers?name=%D7%A9%D7%9C%D7%95%D7%9D", result.Value!.Url.AbsoluteUri);
    }

    [Fact]
    public void Base_path_of_the_destination_is_kept()
    {
        var parameters = new[] { PathParameter("customerId", "5", ParameterDataType.Integer) };

        var result = UpstreamRequestBuilder.Build(parameters, PolicyWith("/customers/{customerId}", baseUrl: "http://crm.internal:8080/api/"));

        Assert.Equal("http://crm.internal:8080/api/customers/5", result.Value!.Url.AbsoluteUri);
    }

    // ---- path traversal ----

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../admin")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("%2e%2e")]
    [InlineData("a%2fb")]
    [InlineData("line\nbreak")]
    [InlineData("")]
    public void Dangerous_path_values_are_rejected(string value)
    {
        var result = UpstreamRequestBuilder.Build(new[] { PathParameter("name", value) }, PolicyWith("/files/{name}"));

        Assert.Equal(RejectionReason.InvalidPathValue, result.Rejection!.Reason);
    }

    [Fact]
    public void Special_characters_in_a_path_value_stay_inside_one_segment()
    {
        var result = UpstreamRequestBuilder.Build(new[] { PathParameter("name", "a b?c#d@e") }, PolicyWith("/files/{name}"));

        Assert.True(result.Passed);
        Assert.Equal("http://localhost:5101/files/a%20b%3Fc%23d%40e", result.Value!.Url.AbsoluteUri);
        Assert.Equal("localhost", result.Value.Url.Host);
    }

    // ---- configuration problems ----

    [Theory]
    [InlineData("TRACE")]
    [InlineData("CONNECT")]
    [InlineData("get")]
    [InlineData("")]
    public void Method_outside_the_allowed_five_is_rejected_as_misconfiguration(string method)
    {
        var result = UpstreamRequestBuilder.Build(Array.Empty<ValidatedParameter>(), PolicyWith("/customers", method));

        Assert.Equal(RejectionReason.ProxyMisconfigured, result.Rejection!.Reason);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://host")]
    [InlineData("")]
    public void Invalid_base_url_is_rejected_as_misconfiguration(string baseUrl)
    {
        var result = UpstreamRequestBuilder.Build(Array.Empty<ValidatedParameter>(), PolicyWith("/customers", baseUrl: baseUrl));

        Assert.Equal(RejectionReason.ProxyMisconfigured, result.Rejection!.Reason);
    }

    [Fact]
    public void Template_that_could_change_the_host_is_rejected()
    {
        var result = UpstreamRequestBuilder.Build(Array.Empty<ValidatedParameter>(), PolicyWith("@evil.example/x"));

        Assert.Equal(RejectionReason.ProxyMisconfigured, result.Rejection!.Reason);
    }

    [Fact]
    public void Template_that_reaches_a_sibling_of_the_base_path_is_rejected()
    {
        // Uri turns /api/../api-admin/x into /api-admin/x, which starts with "/api" but is outside it.
        var result = UpstreamRequestBuilder.Build(Array.Empty<ValidatedParameter>(),
            PolicyWith("/../api-admin/x", baseUrl: "http://crm.internal/api"));

        Assert.Equal(RejectionReason.ProxyMisconfigured, result.Rejection!.Reason);
    }

    [Theory]
    [InlineData("/customers?type=a")]
    [InlineData("/customers#top")]
    public void Template_with_its_own_query_or_fragment_is_rejected(string template)
    {
        var result = UpstreamRequestBuilder.Build(new[] { QueryParameter("page", "2", ParameterDataType.Integer) }, PolicyWith(template));

        Assert.Equal(RejectionReason.ProxyMisconfigured, result.Rejection!.Reason);
    }

    [Fact]
    public void Placeholder_without_a_value_is_rejected_as_misconfiguration()
    {
        var result = UpstreamRequestBuilder.Build(Array.Empty<ValidatedParameter>(), PolicyWith("/customers/{customerId}"));

        Assert.Equal(RejectionReason.ProxyMisconfigured, result.Rejection!.Reason);
    }

    // ---- body ----

    [Fact]
    public void Body_is_built_only_from_body_parameters_with_correct_json_types()
    {
        var parameters = new[]
        {
            BodyParameter("accountId", "7", ParameterDataType.Integer),
            BodyParameter("amount", "10.5", ParameterDataType.Decimal),
            BodyParameter("currency", "ILS", ParameterDataType.String),
            BodyParameter("urgent", "true", ParameterDataType.Boolean),
            QueryParameter("page", "1", ParameterDataType.Integer) // must not appear in the body
        };

        var result = UpstreamRequestBuilder.Build(parameters, PolicyWith("/payments", "POST"));

        Assert.Equal("""{"accountId":7,"amount":10.5,"currency":"ILS","urgent":true}""", result.Value!.BodyJson);
    }

    [Fact]
    public void Quotes_in_a_text_value_cannot_break_the_body()
    {
        var parameters = new[] { BodyParameter("currency", "I\"L\\", ParameterDataType.String) };

        var result = UpstreamRequestBuilder.Build(parameters, PolicyWith("/payments", "POST"));

        using var document = JsonDocument.Parse(result.Value!.BodyJson!);
        Assert.Equal("I\"L\\", document.RootElement.GetProperty("currency").GetString());
    }

    [Fact]
    public void Body_parameters_on_a_GET_operation_are_rejected_as_misconfiguration()
    {
        var parameters = new[] { BodyParameter("accountId", "7", ParameterDataType.Integer) };

        var result = UpstreamRequestBuilder.Build(parameters, PolicyWith("/customers", "GET"));

        Assert.Equal(RejectionReason.ProxyMisconfigured, result.Rejection!.Reason);
    }
}
