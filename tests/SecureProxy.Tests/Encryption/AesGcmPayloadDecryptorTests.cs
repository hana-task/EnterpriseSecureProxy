using System.Security.Cryptography;
using System.Text;
using SecureProxy.Api.Security.Encryption;

namespace SecureProxy.Tests.Encryption;

public class AesGcmPayloadDecryptorTests
{
    private const string KeyRef = "test-key";
    private const string Operation = "customer.get";
    private const int MaxBytes = 64 * 1024;

    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private sealed class FakeKeySource : IKeySource
    {
        public byte[]? GetKey(string keyReference) =>
            keyReference == KeyRef ? (byte[])Key.Clone() : null;
    }

    private readonly AesGcmPayloadDecryptor _sut = new(new FakeKeySource());

    /// <summary>Simulates the client side: encrypts plaintext bound to an operation key.</summary>
    internal static EncryptedPayload Encrypt(string plaintext, byte[] key, string operationKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcmPayloadDecryptor.NonceSizeBytes);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[data.Length];
        var tag = new byte[AesGcmPayloadDecryptor.TagSizeBytes];
        using var aes = new AesGcm(key, AesGcmPayloadDecryptor.TagSizeBytes);
        aes.Encrypt(nonce, data, ciphertext, tag, Encoding.UTF8.GetBytes(operationKey));
        return new EncryptedPayload(Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
    }

    private static string FlipFirstByte(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        bytes[0] ^= 0x01;
        return Convert.ToBase64String(bytes);
    }

    [Fact]
    public void Valid_payload_decrypts_to_original_plaintext()
    {
        var payload = Encrypt("""{"customerId":"123"}""", Key, Operation);

        var result = _sut.Decrypt(payload, KeyRef, Operation, MaxBytes);

        Assert.True(result.Succeeded);
        Assert.Equal("""{"customerId":"123"}""", Encoding.UTF8.GetString(result.Plaintext!));
    }

    [Fact]
    public void Tampered_tag_is_rejected()
    {
        var payload = Encrypt("secret", Key, Operation);
        var tampered = payload with { Tag = FlipFirstByte(payload.Tag!) };

        var result = _sut.Decrypt(tampered, KeyRef, Operation, MaxBytes);

        Assert.Equal(DecryptionFailure.AuthenticationFailed, result.Failure);
        Assert.Null(result.Plaintext);
    }

    [Fact]
    public void Tampered_ciphertext_is_rejected()
    {
        var payload = Encrypt("secret", Key, Operation);
        var tampered = payload with { Ciphertext = FlipFirstByte(payload.Ciphertext!) };

        Assert.Equal(DecryptionFailure.AuthenticationFailed, _sut.Decrypt(tampered, KeyRef, Operation, MaxBytes).Failure);
    }

    [Fact]
    public void Payload_encrypted_for_another_operation_is_rejected()
    {
        var payload = Encrypt("secret", Key, "payment.create");

        Assert.Equal(DecryptionFailure.AuthenticationFailed, _sut.Decrypt(payload, KeyRef, Operation, MaxBytes).Failure);
    }

    [Fact]
    public void Payload_encrypted_with_another_key_is_rejected()
    {
        var payload = Encrypt("secret", RandomNumberGenerator.GetBytes(32), Operation);

        Assert.Equal(DecryptionFailure.AuthenticationFailed, _sut.Decrypt(payload, KeyRef, Operation, MaxBytes).Failure);
    }

    [Fact]
    public void Unknown_key_reference_fails_closed()
    {
        var payload = Encrypt("secret", Key, Operation);

        Assert.Equal(DecryptionFailure.KeyUnavailable, _sut.Decrypt(payload, "no-such-key", Operation, MaxBytes).Failure);
    }

    [Theory]
    [InlineData(null, "AAAA", "AAAAAAAAAAAAAAAAAAAAAA==")]       // missing nonce
    [InlineData("not base64!", "AAAA", "AAAAAAAAAAAAAAAAAAAAAA==")] // bad base64
    [InlineData("AAAA", "AAAA", "AAAAAAAAAAAAAAAAAAAAAA==")]       // nonce too short
    [InlineData("AAAAAAAAAAAAAAAA", "AAAA", "AAAA")]               // tag too short
    public void Malformed_payload_is_rejected(string? nonce, string? ciphertext, string? tag)
    {
        var result = _sut.Decrypt(new EncryptedPayload(nonce, ciphertext, tag), KeyRef, Operation, MaxBytes);

        Assert.Equal(DecryptionFailure.MalformedPayload, result.Failure);
    }

    [Fact]
    public void Oversized_payload_is_rejected_before_decryption()
    {
        var payload = Encrypt(new string('x', 200), Key, Operation);

        Assert.Equal(DecryptionFailure.PayloadTooLarge, _sut.Decrypt(payload, KeyRef, Operation, maxPlaintextBytes: 100).Failure);
    }
}
