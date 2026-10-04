using Client.Licensing.Application;
using Client.Licensing.Domain;
using Licensing.Contracts;
using Security.Es256;

namespace Client.Licensing.Infrastructure;

/// <summary>A trusted issuer public key as configured for licensing (never a private key).</summary>
/// <param name="KeyId">Identifier carried by signed licenses (enables key rotation).</param>
/// <param name="PublicKey">Base64 of the DER SubjectPublicKeyInfo of an ECDSA P-256 public key.</param>
public sealed record TrustedLicenseKey(string KeyId, string PublicKey);

/// <summary>
/// Verifies signed licenses with the neutral ES256 primitives shared with the update system (Security.Es256).
/// The client only ever holds PUBLIC keys. Pure and offline.
/// </summary>
public sealed class EcdsaLicenseVerifier : ILicenseVerifier, IDisposable
{
    private readonly Es256Verifier _verifier;

    public EcdsaLicenseVerifier(IEnumerable<TrustedLicenseKey> trustedKeys)
        => _verifier = new Es256Verifier(trustedKeys.Select(k => new TrustedPublicKey(k.KeyId, k.PublicKey)));

    public LicenseVerificationResult Verify(SignedLicense license)
    {
        if (license is null
            || string.IsNullOrEmpty(license.Payload)
            || string.IsNullOrEmpty(license.Signature)
            || string.IsNullOrEmpty(license.KeyId))
            return LicenseVerificationResult.Invalid(InvalidReason.Malformed);

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

        var check = _verifier.Verify(license.KeyId, license.Algorithm, payloadBytes, signature);
        switch (check)
        {
            case SignatureCheck.UnknownKey:
                return LicenseVerificationResult.Invalid(InvalidReason.UntrustedKey);
            case SignatureCheck.InvalidSignature:
                return LicenseVerificationResult.Invalid(InvalidReason.BadSignature);
            case SignatureCheck.Valid:
                break;
            default:
                return LicenseVerificationResult.Invalid(InvalidReason.Malformed);
        }

        // Only now (signature valid) is the payload parsed and trusted.
        var payload = LicenseSerializer.TryParsePayload(license.Payload);
        if (payload is null || !string.Equals(payload.KeyId, license.KeyId, StringComparison.Ordinal))
            return LicenseVerificationResult.Invalid(InvalidReason.Malformed);

        return LicenseVerificationResult.Valid(payload);
    }

    public void Dispose() => _verifier.Dispose();
}
