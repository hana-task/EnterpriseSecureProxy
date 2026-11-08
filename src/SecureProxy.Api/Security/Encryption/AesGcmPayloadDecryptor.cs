using System.Security.Cryptography;
using System.Text;

namespace SecureProxy.Api.Security.Encryption;

public enum DecryptionFailure
{
    None,
    MalformedPayload,   // missing fields, bad base64, wrong nonce/tag length
    PayloadTooLarge,
    KeyUnavailable,     // key reference unknown or invalid -> fail closed
    AuthenticationFailed // tag mismatch: tampered data, wrong key, or wrong operation (AAD)
}

public sealed record DecryptionResult(byte[]? Plaintext, DecryptionFailure Failure)
{
    public bool Succeeded => Failure == DecryptionFailure.None;
    public static DecryptionResult Fail(DecryptionFailure failure) => new(null, failure);
}

/// <summary>
/// Decrypts AES-256-GCM payloads. GCM is authenticated encryption: if a single bit of the
/// nonce, ciphertext, tag or associated data was changed, decryption fails instead of
/// returning garbage, so a failed result means the request must not be forwarded.
///
/// Associated data (AAD) = the operation key. It is not secret, but it is bound into the tag,
/// so a ciphertext produced for "customer.get" cannot be replayed against "payment.create".
/// </summary>
public sealed class AesGcmPayloadDecryptor
{
    public const int NonceSizeBytes = 12;
    public const int TagSizeBytes = 16;

    private readonly IKeySource _keySource;

    public AesGcmPayloadDecryptor(IKeySource keySource) => _keySource = keySource;

    public DecryptionResult Decrypt(EncryptedPayload? payload, string keyReference, string operationKey, int maxPlaintextBytes)
    {
        if (payload is null
            || !TryDecodeBase64(payload.Nonce, out var nonce)
            || !TryDecodeBase64(payload.Ciphertext, out var ciphertext)
            || !TryDecodeBase64(payload.Tag, out var tag))
            return DecryptionResult.Fail(DecryptionFailure.MalformedPayload);

        if (nonce.Length != NonceSizeBytes || tag.Length != TagSizeBytes)
            return DecryptionResult.Fail(DecryptionFailure.MalformedPayload);

        // GCM plaintext length == ciphertext length, so we can enforce the limit before decrypting.
        if (ciphertext.Length > maxPlaintextBytes)
            return DecryptionResult.Fail(DecryptionFailure.PayloadTooLarge);

        var key = _keySource.GetKey(keyReference);
        if (key is null)
            return DecryptionResult.Fail(DecryptionFailure.KeyUnavailable);

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSizeBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(operationKey));
            return new DecryptionResult(plaintext, DecryptionFailure.None);
        }
        catch (CryptographicException) // includes AuthenticationTagMismatchException
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return DecryptionResult.Fail(DecryptionFailure.AuthenticationFailed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static bool TryDecodeBase64(string? value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(value))
            return false;
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
