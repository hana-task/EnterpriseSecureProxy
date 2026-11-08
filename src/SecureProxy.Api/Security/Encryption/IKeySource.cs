namespace SecureProxy.Api.Security.Encryption;

/// <summary>
/// Abstraction over where encryption keys come from.
/// The database stores only a key *reference* (e.g. "payments-v1"); the key material
/// itself is resolved at runtime from a secret store. This keeps keys out of the DB,
/// out of configuration history, and out of logs.
/// </summary>
public interface IKeySource
{
    /// <summary>
    /// Returns the raw key bytes for the given reference, or null if the reference is unknown
    /// or the stored value is invalid. Callers must treat null as "fail closed".
    /// </summary>
    byte[]? GetKey(string keyReference);
}
