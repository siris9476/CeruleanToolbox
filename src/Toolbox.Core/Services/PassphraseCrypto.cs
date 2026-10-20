using System.Security.Cryptography;
using System.Text;

namespace Toolbox.Core.Services;

/// <summary>
/// Passphrase-based AES-256-GCM encryption for exporting a connection so it can be moved to
/// another machine/user — unlike the DPAPI encryption used for local storage, which is tied to
/// this Windows user account and this machine and therefore isn't portable. GCM is authenticated,
/// so a wrong passphrase or a corrupted/tampered file fails decryption instead of silently
/// producing garbage credentials.
/// </summary>
public static class PassphraseCrypto
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Iterations = 200_000;
    private const int KeySizeBytes = 32; // AES-256

    /// <summary>Envelope layout: [salt(16)][nonce(12)][tag(16)][ciphertext(N)].</summary>
    public static byte[] Encrypt(string plaintext, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(passphrase, salt);

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
        }

        var envelope = new byte[SaltSize + NonceSize + TagSize + cipherBytes.Length];
        Buffer.BlockCopy(salt, 0, envelope, 0, SaltSize);
        Buffer.BlockCopy(nonce, 0, envelope, SaltSize, NonceSize);
        Buffer.BlockCopy(tag, 0, envelope, SaltSize + NonceSize, TagSize);
        Buffer.BlockCopy(cipherBytes, 0, envelope, SaltSize + NonceSize + TagSize, cipherBytes.Length);
        return envelope;
    }

    public static string Decrypt(byte[] envelope, string passphrase)
    {
        if (envelope.Length < SaltSize + NonceSize + TagSize)
        {
            throw new InvalidOperationException(T("Invalid or corrupted export file."));
        }

        var salt = envelope.AsSpan(0, SaltSize).ToArray();
        var nonce = envelope.AsSpan(SaltSize, NonceSize).ToArray();
        var tag = envelope.AsSpan(SaltSize + NonceSize, TagSize).ToArray();
        var cipherBytes = envelope.AsSpan(SaltSize + NonceSize + TagSize).ToArray();

        var key = DeriveKey(passphrase, salt);
        var plainBytes = new byte[cipherBytes.Length];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        }
        catch (CryptographicException)
        {
            // GCM's auth tag check fails identically for a wrong passphrase and for a
            // corrupted/tampered file — there's no way to distinguish the two, which is exactly
            // the point of authenticated encryption.
            throw new InvalidOperationException(T("Wrong passphrase or corrupted export file."));
        }

        return Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Iterations, HashAlgorithmName.SHA256, KeySizeBytes);
}
