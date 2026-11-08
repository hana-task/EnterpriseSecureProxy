using System.Text.Json;
using SecureProxy.Api.Data.Entities;
using SecureProxy.Api.Policies;
using SecureProxy.Api.Validation;

namespace SecureProxy.Tests.Validation;

public class ParameterCheckTests
{
    private static ParameterPolicy Rule(string name, ParameterDataType type, ParameterLocation location = ParameterLocation.Query,
        bool required = true, int? maxLength = null, decimal? min = null, decimal? max = null) => new()
    {
        Name = name, DataType = type, Location = location, IsRequired = required,
        MaxLength = maxLength, MinValue = min, MaxValue = max
    };

    /// <summary>Like customer.get: one required integer in the path.</summary>
    private static OperationPolicy CustomerGet() => TestData.CustomerGetPolicy() with
    {
        Parameters = new[] { Rule("customerId", ParameterDataType.Integer, ParameterLocation.Path, min: 1) }
    };

    private static IReadOnlyDictionary<string, JsonElement> Fields(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static CheckResult<IReadOnlyList<ValidatedParameter>> Check(string json, OperationPolicy policy) =>
        ParameterCheck.Check(Fields(json), policy);

    // ---- required / optional / unknown ----

    [Fact]
    public void Valid_parameter_passes_with_its_location_and_canonical_value()
    {
        var result = Check("""{"customerId":"123"}""", CustomerGet());

        Assert.True(result.Passed);
        var parameter = Assert.Single(result.Value!);
        Assert.Equal("customerId", parameter.Name);
        Assert.Equal(ParameterLocation.Path, parameter.Location);
        Assert.Equal("123", parameter.CanonicalText);
    }

    [Fact]
    public void Missing_required_parameter_is_rejected()
    {
        var result = Check("{}", CustomerGet());
        Assert.Equal(RejectionReason.MissingParameter, result.Rejection!.Reason);
    }

    [Fact]
    public void Optional_parameter_may_be_missing()
    {
        var policy = TestData.CustomerGetPolicy() with
        {
            Parameters = new[] { Rule("page", ParameterDataType.Integer, required: false) }
        };

        var result = Check("{}", policy);

        Assert.True(result.Passed);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public void Path_parameter_is_always_required_even_if_configured_as_optional()
    {
        var policy = TestData.CustomerGetPolicy() with
        {
            Parameters = new[] { Rule("customerId", ParameterDataType.Integer, ParameterLocation.Path, required: false) }
        };

        var result = Check("{}", policy);

        Assert.Equal(RejectionReason.MissingParameter, result.Rejection!.Reason);
    }

    [Theory]
    [InlineData("""{"customerId":"1","debug":true}""")]
    [InlineData("""{"customerId":"1","method":"DELETE"}""")]               // the caller cannot choose a method
    [InlineData("""{"customerId":"1","url":"http://evil.example"}""")]      // nor a URL
    public void Unknown_parameter_is_rejected_by_default(string json)
    {
        var result = Check(json, CustomerGet());
        Assert.Equal(RejectionReason.UnknownParameter, result.Rejection!.Reason);
    }

    [Fact]
    public void Unknown_parameter_is_accepted_but_not_returned_when_the_operation_allows_it()
    {
        var policy = CustomerGet() with { AllowUnknownParameters = true };

        var result = Check("""{"customerId":"1","debug":true}""", policy);

        Assert.True(result.Passed);
        Assert.Equal("customerId", Assert.Single(result.Value!).Name); // "debug" is not forwarded
    }

    [Fact]
    public void Parameter_names_are_case_sensitive()
    {
        var result = Check("""{"CustomerId":"1"}""", CustomerGet());
        Assert.Equal(RejectionReason.MissingParameter, result.Rejection!.Reason);
    }

    // ---- integer ----

    [Theory]
    [InlineData("\"123\"", "123")]
    [InlineData("123", "123")]
    [InlineData("\"007\"", "7")]
    public void Valid_integers_are_accepted_in_canonical_form(string jsonValue, string expected)
    {
        var result = Check($$"""{"customerId":{{jsonValue}}}""", CustomerGet());
        Assert.Equal(expected, Assert.Single(result.Value!).CanonicalText);
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("\"../admin\"")]
    [InlineData("1.5")]
    [InlineData("\"1.5\"")]
    [InlineData("\"1e3\"")]
    [InlineData("\" 12\"")]
    [InlineData("\"\"")]
    [InlineData("0")]            // below the minimum (1)
    [InlineData("-5")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[1]")]
    [InlineData("{\"a\":1}")]
    [InlineData("99999999999999999999")]
    public void Invalid_integers_are_rejected(string jsonValue)
    {
        var result = Check($$"""{"customerId":{{jsonValue}}}""", CustomerGet());
        Assert.Equal(RejectionReason.InvalidParameter, result.Rejection!.Reason);
    }

    [Fact]
    public void Rejection_message_does_not_contain_the_caller_value()
    {
        var result = Check("""{"customerId":"../admin"}""", CustomerGet());

        Assert.DoesNotContain("admin", result.Rejection!.SafeMessage);
        Assert.Contains("customerId", result.Rejection.SafeMessage); // the name comes from configuration
    }

    // ---- decimal ----

    [Theory]
    [InlineData("10.5", true)]
    [InlineData("\"10.50\"", true)]
    [InlineData("0.01", true)]
    [InlineData("0.001", false)]       // below the minimum
    [InlineData("100001", false)]      // above the maximum
    [InlineData("\"abc\"", false)]
    [InlineData("\"1e2\"", false)]
    [InlineData("false", false)]
    public void Decimal_follows_type_and_range(string jsonValue, bool valid)
    {
        var policy = TestData.CustomerGetPolicy() with
        {
            Parameters = new[] { Rule("amount", ParameterDataType.Decimal, ParameterLocation.Body, min: 0.01m, max: 100_000m) }
        };

        var result = Check($$"""{"amount":{{jsonValue}}}""", policy);

        Assert.Equal(valid, result.Passed);
    }

    // ---- string ----

    [Theory]
    [InlineData("\"ILS\"", true)]
    [InlineData("\"\"", true)]
    [InlineData("\"ILSS\"", false)]    // longer than MaxLength 3
    [InlineData("5", false)]           // not a string
    [InlineData("null", false)]
    public void String_follows_type_and_max_length(string jsonValue, bool valid)
    {
        var policy = TestData.CustomerGetPolicy() with
        {
            Parameters = new[] { Rule("currency", ParameterDataType.String, ParameterLocation.Body, maxLength: 3) }
        };

        var result = Check($$"""{"currency":{{jsonValue}}}""", policy);

        Assert.Equal(valid, result.Passed);
    }

    // ---- boolean ----

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData("\"true\"", false)]
    [InlineData("1", false)]
    public void Boolean_accepts_only_json_true_and_false(string jsonValue, bool valid)
    {
        var policy = TestData.CustomerGetPolicy() with
        {
            Parameters = new[] { Rule("active", ParameterDataType.Boolean) }
        };

        var result = Check($$"""{"active":{{jsonValue}}}""", policy);

        Assert.Equal(valid, result.Passed);
    }
}
