using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PalladiumWallet.Core.Storage;

/// <summary>
/// Wallet file encryption (blueprint §8/§17): AES-256-GCM with a key derived
/// from the password via PBKDF2-HMAC-SHA512. The container is self-describing JSON
/// (kdf, parameters, salt, nonce) to allow future parameter upgrades.
/// </summary>
public static class EncryptedFile
{
    private const string Format = "plm-wallet-aesgcm-v1";
    private const int DefaultIterations = 600_000;
    // Upper bound on the iteration count read from the container: the value is
    // attacker-controlled in a tampered file, and an absurd count would hang the
    // wallet at open (PBKDF2 DoS). Leaves ample room for future increases.
    private const int MaxIterations = 10_000_000;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private sealed record Container(
        string Format, int Iterations, string Salt, string Nonce, string Tag, string Data);

    public static bool IsEncrypted(string fileContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(fileContent);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("Format", out var f)
                && f.ValueKind == JsonValueKind.String
                && f.GetString() == Format;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // Invalid UTF-16 (lone surrogates) cannot be transcoded for parsing:
            // certainly not an encrypted container.
            return false;
        }
    }

    public static string Encrypt(string plaintext, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(password, salt, DefaultIterations);

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plainBytes, cipher, tag);
        CryptographicOperations.ZeroMemory(key);

        return JsonSerializer.Serialize(new Container(
            Format, DefaultIterations,
            Convert.ToBase64String(salt), Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag), Convert.ToBase64String(cipher)));
    }

    /// <summary>
    /// Throws <see cref="WrongPasswordException"/> if the password is wrong or the
    /// ciphertext is tampered with, <see cref="InvalidDataException"/> for any
    /// malformed container (broken JSON, missing fields, bad base64, wrong
    /// nonce/tag size, out-of-range iteration count) — never a raw parsing exception.
    /// </summary>
    public static string Decrypt(string fileContent, string password)
    {
        Container? container;
        try
        {
            container = JsonSerializer.Deserialize<Container>(fileContent);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Invalid encrypted container.");
        }
        if (container is null)
            throw new InvalidDataException("Invalid encrypted container.");
        if (container.Format != Format)
            throw new InvalidDataException($"Unknown container format: {container.Format}");
        if (container.Iterations is <= 0 or > MaxIterations)
            throw new InvalidDataException($"Iteration count out of range: {container.Iterations}");

        byte[] salt, nonce, tag, cipher;
        try
        {
            salt   = Convert.FromBase64String(container.Salt);
            nonce  = Convert.FromBase64String(container.Nonce);
            tag    = Convert.FromBase64String(container.Tag);
            cipher = Convert.FromBase64String(container.Data);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentNullException)
        {
            throw new InvalidDataException("Invalid encrypted container.");
        }
        if (nonce.Length != NonceSize || tag.Length != TagSize)
            throw new InvalidDataException("Invalid encrypted container.");

        var key = DeriveKey(password, salt, container.Iterations);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            // The GCM tag authenticates: a wrong password and tampering are indistinguishable.
            throw new WrongPasswordException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, KeySize);
}

/// <summary>Wrong password (or tampered wallet file: the GCM tag does not distinguish the two cases).</summary>
public sealed class WrongPasswordException : Exception
{
    public WrongPasswordException() : base("Wrong password, or the wallet file is damaged.") { }
}
