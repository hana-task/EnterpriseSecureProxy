namespace SecureProxy.Api.Data.Entities;

/// <summary>
/// One allowed parameter of one operation, with its validation rules (assignment 5.3).
/// Example: customer.get has "customerId", an integer, required, placed in the path.
/// </summary>
public class ParameterRule
{
    public int Id { get; set; }

    public int OperationId { get; set; }
    public Operation Operation { get; set; } = null!;

    public string Name { get; set; } = null!;
    public ParameterLocation Location { get; set; }
    public ParameterDataType DataType { get; set; }
    public bool IsRequired { get; set; }

    /// <summary>Maximum text length (String parameters only).</summary>
    public int? MaxLength { get; set; }

    /// <summary>Allowed numeric range (Integer and Decimal parameters only).</summary>
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
}
