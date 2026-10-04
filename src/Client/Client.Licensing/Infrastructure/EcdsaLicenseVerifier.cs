using System.Security.Cryptography;
using Client.Licensing.Application;
using Client.Licensing.Domain;
using Licensing.Contracts;

namespace Client.Licensing.Infrastructure;

/// <summary>A trusted issuer public key (never a private key).</summary>
/// <param name="KeyId">Identifier carried by signed licenses (enables key rotation: trust several keys at once).</param>
/// <param name="PublicKey">Base64 of the DER SubjectPublicKeyInfo of an ECDSA P-256 public key.</param>
public sealed record TrustedLicenseKey(string KeyId, string PublicKey);

/// <summary>
/// Verifies ES256 (ECDSA P-256 / SHA-256, IEEE P1363) license signatures with the standard .NET APIs.
/// The client only ever holds PUBLIC keys. Pure and offline.
/// </summary>
public sealed class EcdsaLicenseVerifier : ILicenseVerifier, IDisposable
{
    private readonly Dictionary<string, ECDsa> _keys = new(StringComparer.Ordinal);

    public EcdsaLicenseVerifier(IEnumerable<TrustedLicenseKey> trustedKeys)
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
                // An unusable trusted key is ignored: licenses signed by it simply fail verification (fail closed).
            }
        }
    }

    public LicenseVerificationResult Verify(SignedLicense license)
    {
        if (license is null
            || string.IsNullOrEmpty(license.Payload)
            || string.IsNullOrEmpty(license.Signature)
            || string.IsNullOrEmpty(license.KeyId))
            return LicenseVerificationResult.Invalid(InvalidReason.Malformed);

        if (!string.Equals(license.Algorithm, LicenseSigning.Algorithm, StringComparison.Ordinal))
            return LicenseVerificationResult.Invalid(InvalidReason.Malformed);

        if (!_keys.TryGetValue(license.KeyId, out var key))
            return LicenseVerificationResult.Invalid(InvalidReason.UntrustedKey);

        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = Convert.FromBase64String(license.Payload);
            signature = Convert.FromBase64String(license.Signature);
        }
        catch (FormatException)
        {
            return LicenseVerificationResult.Invalid(InvalidReason.Malformed);
        }

        bool ok;
        try
        {
            lock (key)
            {
                ok = key.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
        }
        catch (CryptographicException)
        {
            ok = false;
        }

        if (!ok)
            return LicenseVerificationResult.Invalid(InvalidReason.BadSignature);

        // Only now (signature valid) is the payload parsed and trusted.
        var payload = LicenseSerializer.TryParsePayload(license.Payload);
        if (payload is null || !string.Equals(payload.KeyId, license.KeyId, StringComparison.Ordinal))
            return LicenseVerificationResult.Invalid(InvalidReason.Malformed);

        return LicenseVerificationResult.Valid(payload);
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values) key.Dispose();
    }
}
