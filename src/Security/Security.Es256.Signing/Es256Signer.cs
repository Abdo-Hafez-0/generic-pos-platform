using System.Security.Cryptography;
using Security.Es256;

namespace Security.Es256.Signing;

/// <summary>
/// ES256 signer. OWNS the private key; the key is never exposed - only the PUBLIC key can be exported (so it can be
/// distributed to clients as trusted key material). Used only by trusted server/tooling code.
/// </summary>
public sealed class Es256Signer : IDisposable
{
    private readonly ECDsa _key;

    public Es256Signer(ECDsa key, string keyId)
    {
        _key = key;
        KeyId = keyId;
    }

    public string KeyId { get; }

    public string Algorithm => Es256Info.Algorithm;

    public byte[] Sign(byte[] data)
    {
        lock (_key)
        {
            return _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
    }

    /// <summary>Base64 DER SubjectPublicKeyInfo of the public key (safe to share; the TrustedPublicKey format).</summary>
    public string ExportPublicKey() => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    public TrustedPublicKey ToTrustedKey() => new(KeyId, ExportPublicKey());

    /// <summary>Creates a throw-away key (development/testing only). Nothing is persisted or committed.</summary>
    public static Es256Signer GenerateEphemeral(string keyId)
        => new(ECDsa.Create(ECCurve.NamedCurves.nistP256), keyId);

    /// <summary>Loads a PKCS#8 PEM private key from a file OUTSIDE the repository (production key management).</summary>
    public static Es256Signer FromPemFile(string path, string keyId)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(path));
        return new Es256Signer(ecdsa, keyId);
    }

    public void Dispose() => _key.Dispose();
}
