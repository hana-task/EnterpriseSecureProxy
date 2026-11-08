namespace SecureProxy.Api.Data.Entities;

/// <summary>
/// How the parameters of an encrypted operation are protected (assignment 5.5).
/// Holds only a key *reference*. The key itself is resolved at runtime through IKeySource
/// and never stored in the database.
/// </summary>
public class EncryptionProfile
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    /// <summary>Currently always "AES-256-GCM". Kept as data so another algorithm can be added later.</summary>
    public string Algorithm { get; set; } = null!;

    /// <summary>Name of the key in the secret store, e.g. "payments-v1". Not the key.</summary>
    public string KeyReference { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
