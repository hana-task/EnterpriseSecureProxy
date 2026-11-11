using SecureProxy.Api.Admin;

namespace SecureProxy.Tests.Admin;

public class OperationUpdateRequestTests
{
    private static readonly byte[] SomeVersion = { 0, 0, 0, 0, 0, 0, 7, 209 };

    [Fact]
    public void Complete_request_has_no_problem()
    {
        Assert.Null(new OperationUpdateRequest(false, 4096, SomeVersion).FindProblem());
    }

    [Fact]
    public void Largest_allowed_size_is_accepted()
    {
        Assert.Null(new OperationUpdateRequest(true, OperationUpdateRequest.LargestMaxRequestBytes, SomeVersion).FindProblem());
    }

    [Fact]
    public void Missing_is_enabled_is_a_problem()
    {
        Assert.Contains("isEnabled", new OperationUpdateRequest(null, 4096, SomeVersion).FindProblem());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(OperationUpdateRequest.LargestMaxRequestBytes + 1)]
    public void Missing_or_out_of_range_size_is_a_problem(int? maxRequestBytes)
    {
        Assert.Contains("maxRequestBytes", new OperationUpdateRequest(true, maxRequestBytes, SomeVersion).FindProblem());
    }

    [Theory]
    [InlineData(0)] // empty
    [InlineData(7)] // not the 8 bytes of a SQL Server rowversion
    [InlineData(9)]
    public void Row_version_of_the_wrong_length_is_a_problem(int length)
    {
        Assert.Contains("rowVersion", new OperationUpdateRequest(true, 4096, new byte[length]).FindProblem());
    }

    [Fact]
    public void Missing_row_version_is_a_problem()
    {
        Assert.Contains("rowVersion", new OperationUpdateRequest(true, 4096, null).FindProblem());
    }
}
