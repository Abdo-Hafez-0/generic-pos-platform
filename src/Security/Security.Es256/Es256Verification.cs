using System.Security.Cryptography;

namespace Security.Es256;

/// <summary>ES256 = ECDSA over NIST P-256 with SHA-256; signature in IEEE P1363 (r||s) format. Same model for licenses and update packages.</summary>
public static class Es256Info
{
    public const string Algorithm = "ES256";
}

/// <summary>A trusted issuer PUBLIC key (never a private key).</summary>
/// <param name="KeyId">Identifier carried by signed documents (enables key rotation: trust several keys at once).</param>
/// <param name="PublicKey">Base64 of the DER SubjectPublicKeyInfo of an ECDSA P-256 public key.</param>
public sealed record TrustedPublicKey(string KeyId, string PublicKey);

public enum SignatureCheck
{
    Valid = 0,
    Malformed = 1,
    UnsupportedAlgorithm = 2,
    UnknownKey = 3,
    InvalidSignature = 4
}

/// <summary>
/// Verifies ES256 signatures against locally trusted public keys with the standard .NET APIs. Pure and offline;
/// fails closed (no trusted keys, or an unusable key, means nothing verifies). Thread-safe.
/// </summary>
public sealed class Es256Verifier : IDisposable
{
    private readonly Dictionary<string, ECDsa> _keys = new(StringComparer.Ordinal);

    public Es256Verifier(IEnumerable<TrustedPublicKey> trustedKeys)
    {
        foreach (var key in trustedKeys)
        {
            if (string.IsNullOrWhiteSpace(key.KeyId) || string.IsNullOrWhiteSpace(key.PublicKey))
                continue;

            try
            {
                var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKey), out _);
                _keys[key.KeyId] = ecdsa;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // An unusable trusted key is ignored: documents signed by it simply fail verification (fail closed).
            }
        }
    }

    public int TrustedKeyCount => _keys.Count;

    public SignatureCheck Verify(string keyId, string algorithm, byte[] data, byte[] signature)
    {
        if (string.IsNullOrEmpty(keyId) || data is null || signature is null || signature.Length == 0)
            return SignatureCheck.Malformed;

        if (!string.Equals(algorithm, Es256Info.Algorithm, StringComparison.Ordinal))
            return SignatureCheck.UnsupportedAlgorithm;

        if (!_keys.TryGetValue(keyId, out var key))
            return SignatureCheck.UnknownKey;

        try
        {
            lock (key)
            {
                return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                    ? SignatureCheck.Valid
                    : SignatureCheck.InvalidSignature;
            }
        }
        catch (CryptographicException)
        {
            return SignatureCheck.InvalidSignature;
        }
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values) key.Dispose();
    }
}

/// <summary>SHA-256 as lowercase hex. Hash = content identity/integrity; a signature (above) adds authenticity.</summary>
public static class Sha256Hex
{
    public static string Compute(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Compute(Stream stream) => Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}
