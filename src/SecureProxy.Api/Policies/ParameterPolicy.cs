using SecureProxy.Api.Data.Entities;

namespace SecureProxy.Api.Policies;

/// <summary>Read-only copy of one parameter rule.</summary>
public sealed record ParameterPolicy
{
    public required string Name { get; init; }
    public required ParameterLocation Location { get; init; }
    public required ParameterDataType DataType { get; init; }
    public required bool IsRequired { get; init; }
    public int? MaxLength { get; init; }
    public decimal? MinValue { get; init; }
    public decimal? MaxValue { get; init; }
}
