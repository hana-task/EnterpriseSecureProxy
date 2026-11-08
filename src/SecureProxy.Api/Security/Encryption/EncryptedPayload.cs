namespace SecureProxy.Api.Security.Encryption;

/// <summary>
/// Wire format of an AES-GCM encrypted payload. All fields are base64.
/// Nonce: 12 bytes, unique per encryption. Tag: 16 bytes.
/// </summary>
public sealed record EncryptedPayload(string? Nonce, string? Ciphertext, string? Tag);
