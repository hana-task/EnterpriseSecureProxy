namespace SecureProxy.Api.Data.Entities;

/// <summary>
/// One incoming header that may be forwarded to the upstream for one operation (assignment 5.4).
/// Default is deny: a header with no rule is not forwarded. A fixed deny list in code
/// (Authorization, Cookie, hop-by-hop headers...) wins even over a rule in this table.
/// </summary>
public class HeaderRule
{
    public int Id { get; set; }

    public int OperationId { get; set; }
    public Operation Operation { get; set; } = null!;

    public string HeaderName { get; set; } = null!;
}
