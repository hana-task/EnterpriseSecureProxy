namespace SecureProxy.Api.Data.Entities;

/// <summary>
/// An explicitly allowed action (assignment 5.2). This table is the proxy's allowlist:
/// an operation that is not here does not exist.
/// The destination, HTTP method and path come only from this row, never from the caller.
/// </summary>
public class Operation
{
    public int Id { get; set; }

    /// <summary>The key the caller sends as "clientId", e.g. "customer.get". Unique.</summary>
    public string Key { get; set; } = null!;

    /// <summary>
    /// Name of the configured endpoint the caller must send as "urlId". It is a name, never an address.
    /// The request is rejected unless it matches exactly. (Our interpretation of an unclear requirement.)
    /// </summary>
    public string UrlId { get; set; } = null!;

    public int DestinationId { get; set; }
    public Destination Destination { get; set; } = null!;

    /// <summary>GET, POST, PUT, PATCH or DELETE (enforced by a database check constraint).</summary>
    public string HttpMethod { get; set; } = null!;

    /// <summary>Relative path with placeholders, e.g. "/customers/{customerId}".</summary>
    public string PathTemplate { get; set; } = null!;

    public bool IsEnabled { get; set; }

    /// <summary>Largest request body accepted for this operation.</summary>
    public int MaxRequestBytes { get; set; }

    /// <summary>When true, the parameters must arrive AES-GCM encrypted. A plain request is rejected.</summary>
    public bool RequiresEncryption { get; set; }

    /// <summary>Which encryption profile to use. Required when <see cref="RequiresEncryption"/> is true.</summary>
    public int? EncryptionProfileId { get; set; }
    public EncryptionProfile? EncryptionProfile { get; set; }

    /// <summary>When false (the default), a parameter without a rule rejects the request.</summary>
    public bool AllowUnknownParameters { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>SQL Server rowversion, the configuration concurrency token required by 5.2.</summary>
    public byte[] RowVersion { get; set; } = null!;

    public List<ParameterRule> ParameterRules { get; set; } = new();
    public List<HeaderRule> HeaderRules { get; set; } = new();
}
