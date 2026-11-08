using System.Security.Cryptography;
using System.Text.Json;
using SecureProxy.Api.Policies;
using SecureProxy.Api.Security.Encryption;

namespace SecureProxy.Api.Validation;

/// <summary>
/// Check 4: produces the parameters the next checks will validate.
///
/// - Operation without encryption: the fields are the parameters, as they came.
/// - Operation that requires encryption: the fields must be exactly nonce, ciphertext and tag.
///   They are decrypted, and the decrypted JSON object is the parameters.
///   A plain (unencrypted) request to such an operation is rejected, otherwise encryption
///   could be bypassed by simply not using it.
/// </summary>
public static class DecryptionCheck
{
    private const string SupportedAlgorithm = "AES-256-GCM";

    // One message for every encrypted-payload failure, so the caller cannot tell which step failed
    // (bad structure, wrong tag, or content that is not JSON). The reason stays precise for the audit log.
    private const string InvalidPayloadMessage = "The encrypted payload is invalid.";

    public static CheckResult<IReadOnlyDictionary<string, JsonElement>> Check(
        IReadOnlyDictionary<string, JsonElement> fields, OperationPolicy policy, AesGcmPayloadDecryptor decryptor)
    {
        if (!policy.RequiresEncryption)
            return CheckResult<IReadOnlyDictionary<string, JsonElement>>.Pass(fields);

        // The operation says "encrypted", so the configuration must say how. If it doesn't, that is our
        // mistake, not the caller's, but we still do not forward.
        if (policy.EncryptionKeyReference is null || policy.EncryptionAlgorithm != SupportedAlgorithm)
            return Reject(RejectionReason.ProxyMisconfigured, "The operation is not configured correctly.");

        var hasAnyEncryptedField = fields.ContainsKey("nonce") || fields.ContainsKey("ciphertext") || fields.ContainsKey("tag");
        if (!hasAnyEncryptedField)
            return Reject(RejectionReason.EncryptionRequired, "This operation requires an encrypted payload.");

        if (!TryReadPayload(fields, out var payload))
            return Reject(RejectionReason.InvalidEncryptedPayload, InvalidPayloadMessage);

        // The size limit is applied before decrypting, so a huge payload is never processed.
        var decryption = decryptor.Decrypt(payload, policy.EncryptionKeyReference, policy.Key, policy.MaxRequestBytes);
        if (!decryption.Succeeded)
            return Reject(MapFailure(decryption.Failure), InvalidPayloadMessage);

        try
        {
            return ParseParameters(decryption.Plaintext!);
        }
        finally
        {
            // Wipes the raw decrypted bytes as soon as they are parsed. The parsed values themselves
            // live on until the request ends (they must be forwarded), so this shortens exposure,
            // it does not remove the data from memory.
            CryptographicOperations.ZeroMemory(decryption.Plaintext);
        }
    }

    /// <summary>Exactly three fields, all strings. Anything else is not a valid encrypted payload.</summary>
    private static bool TryReadPayload(IReadOnlyDictionary<string, JsonElement> fields, out EncryptedPayload payload)
    {
        payload = new EncryptedPayload(null, null, null);
        if (fields.Count != 3
            || !TryReadString(fields, "nonce", out var nonce)
            || !TryReadString(fields, "ciphertext", out var ciphertext)
            || !TryReadString(fields, "tag", out var tag))
            return false;

        payload = new EncryptedPayload(nonce, ciphertext, tag);
        return true;
    }

    private static bool TryReadString(IReadOnlyDictionary<string, JsonElement> fields, string name, out string? value)
    {
        value = null;
        if (!fields.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString();
        return true;
    }

    /// <summary>The decrypted text must itself be a JSON object (without duplicate fields).</summary>
    private static CheckResult<IReadOnlyDictionary<string, JsonElement>> ParseParameters(byte[] plaintext)
    {
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            if (!JsonObjectHelper.IsValidObject(root))
                return Reject(RejectionReason.InvalidEncryptedPayload, InvalidPayloadMessage);

            var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                parameters[property.Name] = property.Value.Clone();

            return CheckResult<IReadOnlyDictionary<string, JsonElement>>.Pass(parameters);
        }
        catch (JsonException)
        {
            return Reject(RejectionReason.InvalidEncryptedPayload, InvalidPayloadMessage);
        }
    }

    private static RejectionReason MapFailure(DecryptionFailure failure) => failure switch
    {
        DecryptionFailure.MalformedPayload => RejectionReason.InvalidEncryptedPayload,
        DecryptionFailure.PayloadTooLarge => RejectionReason.RequestTooLarge,
        DecryptionFailure.KeyUnavailable => RejectionReason.ProxyMisconfigured,
        _ => RejectionReason.DecryptionFailed // AuthenticationFailed: tampered, wrong key or wrong operation
    };

    private static CheckResult<IReadOnlyDictionary<string, JsonElement>> Reject(RejectionReason reason, string message) =>
        CheckResult<IReadOnlyDictionary<string, JsonElement>>.Reject(reason, message);
}
