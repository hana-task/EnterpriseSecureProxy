using System.Security.Cryptography;
using System.Text.Json;
using SecureProxy.Api.Security.Encryption;
using SecureProxy.Api.Validation;
using SecureProxy.Tests.Encryption;

namespace SecureProxy.Tests.Validation;

public class DecryptionCheckTests
{
    private const string KeyReference = "payments-v1";
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private sealed class FakeKeySource : IKeySource
    {
        public byte[]? GetKey(string keyReference) => keyReference == KeyReference ? (byte[])Key.Clone() : null;
    }

    private static readonly AesGcmPayloadDecryptor Decryptor = new(new FakeKeySource());

    private static Api.Policies.OperationPolicy EncryptedPolicy() => TestData.CustomerGetPolicy() with
    {
        Key = "payment.create",
        RequiresEncryption = true,
        EncryptionAlgorithm = "AES-256-GCM",
        EncryptionKeyReference = KeyReference
    };

    private static IReadOnlyDictionary<string, JsonElement> Fields(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    /// <summary>The fields a client would send: the encrypted payload, as JSON fields.</summary>
    private static IReadOnlyDictionary<string, JsonElement> EncryptedFields(
        string plaintext, string operationKey = "payment.create")
    {
        var payload = AesGcmPayloadDecryptorTests.Encrypt(plaintext, Key, operationKey);
        return Fields(JsonSerializer.Serialize(new { nonce = payload.Nonce, ciphertext = payload.Ciphertext, tag = payload.Tag }));
    }

    [Fact]
    public void Operation_without_encryption_passes_the_fields_through_unchanged()
    {
        var fields = Fields("""{"customerId":"123"}""");

        var result = DecryptionCheck.Check(fields, TestData.CustomerGetPolicy(), Decryptor);

        Assert.True(result.Passed);
        Assert.Equal("123", result.Value!["customerId"].GetString());
    }

    [Fact]
    public void Valid_encrypted_payload_is_decrypted_into_parameters()
    {
        var fields = EncryptedFields("""{"accountId":7,"amount":10.5}""");

        var result = DecryptionCheck.Check(fields, EncryptedPolicy(), Decryptor);

        Assert.True(result.Passed);
        Assert.Equal(7, result.Value!["accountId"].GetInt32());
    }

    [Fact]
    public void Plain_request_to_an_operation_that_requires_encryption_is_rejected()
    {
        var result = DecryptionCheck.Check(Fields("""{"accountId":7,"amount":10.5}"""), EncryptedPolicy(), Decryptor);

        Assert.Equal(RejectionReason.EncryptionRequired, result.Rejection!.Reason);
    }

    [Fact]
    public void Tampered_tag_is_rejected()
    {
        var payload = AesGcmPayloadDecryptorTests.Encrypt("""{"accountId":7}""", Key, "payment.create");
        var tag = Convert.FromBase64String(payload.Tag!);
        tag[0] ^= 0x01;
        var fields = Fields(JsonSerializer.Serialize(
            new { nonce = payload.Nonce, ciphertext = payload.Ciphertext, tag = Convert.ToBase64String(tag) }));

        var result = DecryptionCheck.Check(fields, EncryptedPolicy(), Decryptor);

        Assert.Equal(RejectionReason.DecryptionFailed, result.Rejection!.Reason);
    }

    [Fact]
    public void Payload_encrypted_for_another_operation_is_rejected()
    {
        var fields = EncryptedFields("""{"accountId":7}""", operationKey: "customer.get");

        var result = DecryptionCheck.Check(fields, EncryptedPolicy(), Decryptor);

        Assert.Equal(RejectionReason.DecryptionFailed, result.Rejection!.Reason);
    }

    [Theory]
    [InlineData("""{"nonce":"AAAA","ciphertext":"AAAA"}""")]                       // tag missing
    [InlineData("""{"nonce":"!!!","ciphertext":"AAAA","tag":"AAAA"}""")]           // not base64
    [InlineData("""{"nonce":"AAAA","ciphertext":"AAAA","tag":"AAAA"}""")]          // wrong lengths
    [InlineData("""{"nonce":1,"ciphertext":"AAAA","tag":"AAAA"}""")]               // not a string
    public void Malformed_encrypted_payload_is_rejected(string json)
    {
        var result = DecryptionCheck.Check(Fields(json), EncryptedPolicy(), Decryptor);

        Assert.Equal(RejectionReason.InvalidEncryptedPayload, result.Rejection!.Reason);
    }

    [Fact]
    public void Extra_field_next_to_the_encrypted_payload_is_rejected()
    {
        var payload = AesGcmPayloadDecryptorTests.Encrypt("""{"accountId":7}""", Key, "payment.create");
        var fields = Fields(JsonSerializer.Serialize(
            new { nonce = payload.Nonce, ciphertext = payload.Ciphertext, tag = payload.Tag, accountId = 8 }));

        var result = DecryptionCheck.Check(fields, EncryptedPolicy(), Decryptor);

        Assert.Equal(RejectionReason.InvalidEncryptedPayload, result.Rejection!.Reason);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"a":1,"a":2}""")]
    [InlineData("""{"a":"\uD800"}""")]   // invalid text inside the encrypted content

    public void Decrypted_content_that_is_not_a_json_object_is_rejected(string plaintext)
    {
        var result = DecryptionCheck.Check(EncryptedFields(plaintext), EncryptedPolicy(), Decryptor);

        Assert.Equal(RejectionReason.InvalidEncryptedPayload, result.Rejection!.Reason);
    }

    [Fact]
    public void Caller_gets_the_same_message_for_a_wrong_tag_and_a_malformed_payload()
    {
        var payload = AesGcmPayloadDecryptorTests.Encrypt("""{"accountId":7}""", Key, "payment.create");
        var tag = Convert.FromBase64String(payload.Tag!);
        tag[0] ^= 0x01;
        var wrongTag = Fields(JsonSerializer.Serialize(
            new { nonce = payload.Nonce, ciphertext = payload.Ciphertext, tag = Convert.ToBase64String(tag) }));
        var malformed = Fields("""{"nonce":"!!!","ciphertext":"AAAA","tag":"AAAA"}""");

        var wrongTagResult = DecryptionCheck.Check(wrongTag, EncryptedPolicy(), Decryptor);
        var malformedResult = DecryptionCheck.Check(malformed, EncryptedPolicy(), Decryptor);

        // Different reasons for the audit log, identical message for the caller.
        Assert.NotEqual(wrongTagResult.Rejection!.Reason, malformedResult.Rejection!.Reason);
        Assert.Equal(wrongTagResult.Rejection.SafeMessage, malformedResult.Rejection.SafeMessage);
    }

    [Fact]
    public void Missing_key_fails_closed()
    {
        var policy = EncryptedPolicy() with { EncryptionKeyReference = "unknown-key" };

        var result = DecryptionCheck.Check(EncryptedFields("""{"accountId":7}"""), policy, Decryptor);

        Assert.Equal(RejectionReason.ProxyMisconfigured, result.Rejection!.Reason);
    }

    [Fact]
    public void Operation_that_requires_encryption_without_a_profile_is_rejected()
    {
        var policy = EncryptedPolicy() with { EncryptionKeyReference = null, EncryptionAlgorithm = null };

        var result = DecryptionCheck.Check(EncryptedFields("""{"accountId":7}"""), policy, Decryptor);

        Assert.Equal(RejectionReason.ProxyMisconfigured, result.Rejection!.Reason);
    }
}
