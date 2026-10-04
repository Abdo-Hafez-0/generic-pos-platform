using System.Security.Cryptography;
using System.Text;
using Client.Licensing.Domain;
using Client.Licensing.Infrastructure;
using LicenseServer.Infrastructure;
using Licensing.Contracts;
using Platform.Core.Licensing;
using Platform.Core.Modules;

namespace Licensing.Tests;

public sealed class EvaluatorAndSignatureTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Install = Guid.NewGuid();
    private const string Product = "genericpos";

    private static LicensePayload Payload(
        LicenseStatusClaim status = LicenseStatusClaim.Active,
        Guid? installation = null,
        string product = Product,
        string[]? modules = null,
        string[]? features = null) => new(
            LicenseId: Guid.NewGuid(),
            CustomerId: "c1",
            InstallationId: installation ?? Install,
            ProductId: product,
            LicenseVersion: 1,
            IssuedAt: T0,
            ValidFrom: T0,
            ValidUntil: T0.AddDays(365),
            LeaseValidUntil: T0.AddDays(30),
            GracePeriodUntil: T0.AddDays(37),
            Status: status,
            Modules: modules ?? ["pos", "catalog"],
            Features: features ?? ["advancedreports"],
            Issuer: "issuer",
            KeyId: "k");

    private static LicenseEvaluation Eval(LicensePayload p, DateTimeOffset now, LicensePolicy? policy = null)
        => LicenseEvaluator.Evaluate(true, LicenseVerificationResult.Valid(p), Install, Product, now, policy ?? new LicensePolicy());

    // --- States ---

    [Fact]
    public void NoLicense_IsUnlicensed()
    {
        var e = LicenseEvaluator.Evaluate(false, null, Install, Product, T0, new LicensePolicy());

        Assert.Equal(LicenseState.Unlicensed, e.State);
        Assert.False(e.GrantsEntitlements);
        Assert.False(e.IsModuleLicensed(new ModuleId("pos")));
    }

    [Fact]
    public void ValidLicense_InsideLease_IsActive_AndGrantsEntitlements()
    {
        var e = Eval(Payload(), T0.AddDays(10));

        Assert.Equal(LicenseState.Active, e.State);
        Assert.True(e.GrantsEntitlements);
    }

    [Fact]
    public void AfterLease_InsideGrace_IsGracePeriod()
    {
        var e = Eval(Payload(), T0.AddDays(33));

        Assert.Equal(LicenseState.GracePeriod, e.State);
        Assert.True(e.GrantsEntitlements);
    }

    [Fact]
    public void GracePeriod_CanBeConfiguredToRestrictEntitlements()
    {
        var e = Eval(Payload(), T0.AddDays(33), new LicensePolicy(GraceGrantsEntitlements: false));

        Assert.Equal(LicenseState.GracePeriod, e.State);
        Assert.False(e.GrantsEntitlements);
        Assert.False(e.IsModuleLicensed(new ModuleId("pos")));
    }

    [Fact]
    public void AfterLeaseAndGrace_IsExpired_LeaseExpired()
    {
        var e = Eval(Payload(), T0.AddDays(38));

        Assert.Equal(LicenseState.Expired, e.State);
        Assert.Equal(ExpiryKind.LeaseExpired, e.ExpiryKind);
        Assert.False(e.GrantsEntitlements);
    }

    [Fact]
    public void AfterCommercialValidUntil_IsExpired_LicenseExpired_EvenIfLeaseWouldCoverIt()
    {
        var p = Payload() with { LeaseValidUntil = T0.AddDays(400), GracePeriodUntil = T0.AddDays(410) };

        var e = Eval(p, T0.AddDays(366));

        Assert.Equal(LicenseState.Expired, e.State);
        Assert.Equal(ExpiryKind.LicenseExpired, e.ExpiryKind);
    }

    [Fact]
    public void SignedSuspended_IsSuspended()
    {
        var e = Eval(Payload(LicenseStatusClaim.Suspended), T0.AddDays(1));

        Assert.Equal(LicenseState.Suspended, e.State);
        Assert.False(e.GrantsEntitlements);
    }

    [Fact]
    public void SignedRevoked_IsRevoked_EvenWhenOtherwiseExpired()
    {
        var e = Eval(Payload(LicenseStatusClaim.Revoked), T0.AddDays(900));

        Assert.Equal(LicenseState.Revoked, e.State);
        Assert.False(e.GrantsEntitlements);
    }

    [Fact]
    public void NotYetValid_IsInvalid_NotExpired()
    {
        var e = Eval(Payload(), T0.AddSeconds(-1));

        Assert.Equal(LicenseState.Invalid, e.State);
        Assert.Equal(InvalidReason.NotYetValid, e.InvalidReason);
    }

    [Fact]
    public void WrongInstallation_IsInvalid_NotExpiredOrRevoked()
    {
        var e = Eval(Payload(installation: Guid.NewGuid()), T0.AddDays(1));

        Assert.Equal(LicenseState.Invalid, e.State);
        Assert.Equal(InvalidReason.WrongInstallation, e.InvalidReason);
        Assert.Null(e.Payload);
    }

    [Fact]
    public void WrongProduct_IsInvalid()
    {
        var e = Eval(Payload(product: "other"), T0.AddDays(1));

        Assert.Equal(LicenseState.Invalid, e.State);
        Assert.Equal(InvalidReason.WrongProduct, e.InvalidReason);
    }

    [Theory]
    [InlineData(InvalidReason.BadSignature)]
    [InlineData(InvalidReason.UntrustedKey)]
    [InlineData(InvalidReason.Malformed)]
    public void FailedVerification_IsInvalid_WithReason(InvalidReason reason)
    {
        var e = LicenseEvaluator.Evaluate(true, LicenseVerificationResult.Invalid(reason), Install, Product, T0, new LicensePolicy());

        Assert.Equal(LicenseState.Invalid, e.State);
        Assert.Equal(reason, e.InvalidReason);
        Assert.False(e.GrantsEntitlements);
    }

    [Fact]
    public void InvalidSignature_IsDistinctFromExpired()
    {
        var invalid = LicenseEvaluator.Evaluate(true, LicenseVerificationResult.Invalid(InvalidReason.BadSignature), Install, Product, T0, new LicensePolicy());
        var expired = Eval(Payload(), T0.AddDays(500));

        Assert.NotEqual(invalid.State, expired.State);
    }

    // --- Boundaries (end instants are inclusive) ---

    [Fact]
    public void Boundaries_AreInclusive()
    {
        var p = Payload();

        Assert.Equal(LicenseState.Active, Eval(p, p.ValidFrom).State);                       // exactly ValidFrom
        Assert.Equal(LicenseState.Active, Eval(p, p.LeaseValidUntil).State);                 // exactly lease end
        Assert.Equal(LicenseState.GracePeriod, Eval(p, p.LeaseValidUntil.AddTicks(1)).State); // just after lease
        Assert.Equal(LicenseState.GracePeriod, Eval(p, p.GracePeriodUntil).State);           // exactly grace end
        Assert.Equal(LicenseState.Expired, Eval(p, p.GracePeriodUntil.AddTicks(1)).State);   // just after grace
    }

    [Fact]
    public void ValidUntilBoundary_IsInclusive()
    {
        var p = Payload() with { LeaseValidUntil = T0.AddDays(365), GracePeriodUntil = T0.AddDays(365) };

        Assert.Equal(LicenseState.Active, Eval(p, p.ValidUntil).State);
        Assert.Equal(LicenseState.Expired, Eval(p, p.ValidUntil.AddTicks(1)).State);
    }

    // --- Entitlements ---

    [Fact]
    public void Entitlements_ModulesAndFeatures_AreLicensedOnlyWhenInSignedLicense()
    {
        var e = Eval(Payload(modules: ["POS", " catalog "], features: ["AdvancedReports"]), T0.AddDays(1));

        Assert.True(e.IsModuleLicensed(new ModuleId("pos")));
        Assert.True(e.IsModuleLicensed(new ModuleId("Catalog")));
        Assert.False(e.IsModuleLicensed(new ModuleId("accounting")));   // no Accounting module exists anywhere
        Assert.True(e.IsFeatureLicensed(new FeatureId("advancedreports")));
        Assert.False(e.IsFeatureLicensed(new FeatureId("multibranch")));
    }

    [Fact]
    public void Entitlements_AreDeniedInEveryNonGrantingState()
    {
        var module = new ModuleId("pos");
        Assert.False(Eval(Payload(LicenseStatusClaim.Suspended), T0.AddDays(1)).IsModuleLicensed(module));
        Assert.False(Eval(Payload(LicenseStatusClaim.Revoked), T0.AddDays(1)).IsModuleLicensed(module));
        Assert.False(Eval(Payload(), T0.AddDays(999)).IsModuleLicensed(module));
        Assert.False(Eval(Payload(installation: Guid.NewGuid()), T0.AddDays(1)).IsModuleLicensed(module));
    }

    // --- Signature verification ---

    private static (EcdsaLicenseSigner Signer, EcdsaLicenseVerifier Verifier, SignedLicense License) SignedFixture(LicensePayload? payload = null)
    {
        var signer = EcdsaLicenseSigner.GenerateEphemeral("k");
        var verifier = new EcdsaLicenseVerifier([new TrustedLicenseKey("k", signer.ExportPublicKey())]);
        var bytes = LicenseSerializer.SerializePayloadBytes(payload ?? Payload());
        var license = new SignedLicense(
            LicenseSerializer.ToPayloadText(bytes), "k", signer.Algorithm, Convert.ToBase64String(signer.Sign(bytes)));
        return (signer, verifier, license);
    }

    [Fact]
    public void ValidSignature_IsAccepted_AndPayloadIsParsed()
    {
        var original = Payload();
        var (_, verifier, license) = SignedFixture(original);

        var result = verifier.Verify(license);

        Assert.True(result.IsValid);
        Assert.Equal(original.LicenseId, result.Payload!.LicenseId);
        Assert.Equal(original.Modules, result.Payload.Modules);
    }

    [Fact]
    public void ModifiedPayload_IsRejected_AsBadSignature()
    {
        var (_, verifier, license) = SignedFixture();
        // An attacker extends the expiry and re-encodes the payload, keeping the old signature.
        var forged = LicenseSerializer.TryParsePayload(license.Payload)! with { ValidUntil = T0.AddYears(50), LeaseValidUntil = T0.AddYears(50) };
        var tampered = license with { Payload = LicenseSerializer.ToPayloadText(LicenseSerializer.SerializePayloadBytes(forged)) };

        var result = verifier.Verify(tampered);

        Assert.False(result.IsValid);
        Assert.Equal(InvalidReason.BadSignature, result.Reason);
        Assert.Null(result.Payload);
    }

    [Fact]
    public void SingleFlippedPayloadByte_IsRejected()
    {
        var (_, verifier, license) = SignedFixture();
        var bytes = Convert.FromBase64String(license.Payload);
        bytes[bytes.Length / 2] ^= 0x01;

        var result = verifier.Verify(license with { Payload = Convert.ToBase64String(bytes) });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ModifiedSignature_IsRejected()
    {
        var (_, verifier, license) = SignedFixture();
        var sig = Convert.FromBase64String(license.Signature);
        sig[0] ^= 0xFF;

        var result = verifier.Verify(license with { Signature = Convert.ToBase64String(sig) });

        Assert.False(result.IsValid);
        Assert.Equal(InvalidReason.BadSignature, result.Reason);
    }

    [Fact]
    public void WrongPublicKey_IsRejected()
    {
        var (_, _, license) = SignedFixture();
        using var other = EcdsaLicenseSigner.GenerateEphemeral("k");
        var verifier = new EcdsaLicenseVerifier([new TrustedLicenseKey("k", other.ExportPublicKey())]);

        var result = verifier.Verify(license);

        Assert.False(result.IsValid);
        Assert.Equal(InvalidReason.BadSignature, result.Reason);
    }

    [Fact]
    public void UnknownKeyId_IsRejected_AsUntrusted()
    {
        var (signer, _, license) = SignedFixture();
        var verifier = new EcdsaLicenseVerifier([new TrustedLicenseKey("different-id", signer.ExportPublicKey())]);

        Assert.Equal(InvalidReason.UntrustedKey, verifier.Verify(license).Reason);
    }

    [Fact]
    public void NoTrustedKeys_FailsClosed()
    {
        var (_, _, license) = SignedFixture();

        Assert.False(new EcdsaLicenseVerifier([]).Verify(license).IsValid);
    }

    [Fact]
    public void KeyRotation_MultipleTrustedKeys_AcceptLicensesFromEither()
    {
        var (signerA, _, licenseA) = SignedFixture();
        using var signerB = EcdsaLicenseSigner.GenerateEphemeral("k2");
        var bytesB = LicenseSerializer.SerializePayloadBytes(Payload() with { KeyId = "k2" });
        var licenseB = new SignedLicense(LicenseSerializer.ToPayloadText(bytesB), "k2", signerB.Algorithm, Convert.ToBase64String(signerB.Sign(bytesB)));
        var verifier = new EcdsaLicenseVerifier([
            new TrustedLicenseKey("k", signerA.ExportPublicKey()),
            new TrustedLicenseKey("k2", signerB.ExportPublicKey())]);

        Assert.True(verifier.Verify(licenseA).IsValid);
        Assert.True(verifier.Verify(licenseB).IsValid);
    }

    [Fact]
    public void KeyIdOnEnvelope_MustMatchKeyIdInSignedPayload()
    {
        var (signer, _, _) = SignedFixture();
        var verifier = new EcdsaLicenseVerifier([new TrustedLicenseKey("k", signer.ExportPublicKey())]);
        var bytes = LicenseSerializer.SerializePayloadBytes(Payload() with { KeyId = "someone-else" });
        var license = new SignedLicense(LicenseSerializer.ToPayloadText(bytes), "k", signer.Algorithm, Convert.ToBase64String(signer.Sign(bytes)));

        Assert.False(verifier.Verify(license).IsValid);
    }

    [Theory]
    [InlineData("", "k", "ES256", "AAAA")]
    [InlineData("AAAA", "", "ES256", "AAAA")]
    [InlineData("AAAA", "k", "ES256", "")]
    [InlineData("AAAA", "k", "none", "AAAA")]
    [InlineData("not base64!!", "k", "ES256", "AAAA")]
    public void MalformedEnvelope_IsRejected(string payload, string keyId, string algorithm, string signature)
    {
        var (_, verifier, _) = SignedFixture();

        var result = verifier.Verify(new SignedLicense(payload, keyId, algorithm, signature));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void UnusableTrustedKey_IsIgnored_AndFailsClosed()
    {
        var verifier = new EcdsaLicenseVerifier([new TrustedLicenseKey("k", "###not-a-key###")]);
        var (_, _, license) = SignedFixture();

        Assert.False(verifier.Verify(license).IsValid);
    }

    [Fact]
    public void Signer_PrivateKey_IsNeverPartOfTheExportedPublicKey()
    {
        using var signer = EcdsaLicenseSigner.GenerateEphemeral("k");
        var spki = Convert.FromBase64String(signer.ExportPublicKey());
        using var imported = ECDsa.Create();
        imported.ImportSubjectPublicKeyInfo(spki, out _);

        Assert.Throws<CryptographicException>(() => imported.ExportPkcs8PrivateKey());
        Assert.Throws<CryptographicException>(() => imported.SignData(Encoding.UTF8.GetBytes("x"), HashAlgorithmName.SHA256));
    }
}
