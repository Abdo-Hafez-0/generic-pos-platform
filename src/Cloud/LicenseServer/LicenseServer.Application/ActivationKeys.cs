using System.Security.Cryptography;
using System.Text;

namespace LicenseServer.Application;

/// <summary>Generation and storage form of activation keys. The server stores only the hash; the key is shown once.</summary>
public static class ActivationKeys
{
    // 32 symbols without the look-alikes 0/O/1/I; 25 symbols = 125 bits of entropy.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Generate()
    {
        var chars = new char[29];
        var index = 0;
        for (var i = 0; i < 25; i++)
        {
            if (i > 0 && i % 5 == 0) chars[index++] = '-';
            chars[index++] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>SHA-256 (lowercase hex) of the trimmed key: what durable repositories store and look up by.</summary>
    public static string Hash(string activationKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(activationKey.Trim()))).ToLowerInvariant();
}
