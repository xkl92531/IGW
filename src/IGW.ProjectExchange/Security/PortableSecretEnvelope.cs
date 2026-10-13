using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IGW.ProjectExchange.Security;

public static class PortableSecretEnvelope
{
    private const int Iterations = 310_000;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int KeySize = 32;
    private const int TagSize = 16;
    private static readonly byte[] AssociatedData = "IGW portable project secrets v1"u8.ToArray();

    public static byte[] Encrypt(IReadOnlyDictionary<string, string> secrets, string password)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(secrets);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);
            return JsonSerializer.SerializeToUtf8Bytes(new Envelope(
                1,
                "pbkdf2-sha256",
                Iterations,
                Convert.ToBase64String(salt),
                "aes-256-gcm",
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(tag),
                Convert.ToBase64String(ciphertext)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static IReadOnlyDictionary<string, string> Decrypt(ReadOnlySpan<byte> encoded, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        Envelope envelope = JsonSerializer.Deserialize<Envelope>(encoded)
            ?? throw new CryptographicException("portable_secret_envelope_invalid");
        Validate(envelope);
        byte[] salt = Decode(envelope.Salt, SaltSize);
        byte[] nonce = Decode(envelope.Nonce, NonceSize);
        byte[] tag = Decode(envelope.Tag, TagSize);
        byte[] ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        byte[] plaintext = new byte[ciphertext.Length];
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, envelope.Iterations, HashAlgorithmName.SHA256, KeySize);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext)
                ?? throw new CryptographicException("portable_secret_payload_invalid");
        }
        catch (AuthenticationTagMismatchException error)
        {
            throw new CryptographicException("portable_secret_authentication_failed", error);
        }
        catch (JsonException error)
        {
            throw new CryptographicException("portable_secret_payload_invalid", error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void Validate(Envelope envelope)
    {
        if (envelope.Version != 1
            || envelope.Kdf != "pbkdf2-sha256"
            || envelope.Iterations is < 100_000 or > 1_000_000
            || envelope.Cipher != "aes-256-gcm")
            throw new CryptographicException("portable_secret_envelope_unsupported");
    }

    private static byte[] Decode(string value, int expectedLength)
    {
        byte[] result;
        try { result = Convert.FromBase64String(value); }
        catch (FormatException error) { throw new CryptographicException("portable_secret_envelope_invalid", error); }
        if (result.Length != expectedLength)
            throw new CryptographicException("portable_secret_envelope_invalid");
        return result;
    }

    private sealed record Envelope(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("kdf")] string Kdf,
        [property: JsonPropertyName("iterations")] int Iterations,
        [property: JsonPropertyName("salt")] string Salt,
        [property: JsonPropertyName("cipher")] string Cipher,
        [property: JsonPropertyName("nonce")] string Nonce,
        [property: JsonPropertyName("tag")] string Tag,
        [property: JsonPropertyName("ciphertext")] string Ciphertext);
}
