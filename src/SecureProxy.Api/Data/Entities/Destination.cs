namespace SecureProxy.Api.Data.Entities;

/// <summary>
/// An upstream API the proxy is allowed to call (assignment 5.1).
/// The real address lives only here; callers never send an address.
/// </summary>
public class Destination
{
    public int Id { get; set; }

    /// <summary>Unique, human-readable name, e.g. "crm".</summary>
    public string Name { get; set; } = null!;

    /// <summary>Absolute http/https address, e.g. "https://crm.internal". Scheme, host and port are fixed here.</summary>
    public string BaseUrl { get; set; } = null!;

    /// <summary>A disabled destination rejects every operation that points to it.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>How long to wait for the upstream before returning 504 Gateway Timeout.</summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>Largest upstream response the proxy will pass back to the caller.</summary>
    public long MaxResponseBytes { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>SQL Server rowversion. Changes automatically on every update; used to detect stale updates (409).</summary>
    public byte[] RowVersion { get; set; } = null!;

    public List<Operation> Operations { get; set; } = new();
}
