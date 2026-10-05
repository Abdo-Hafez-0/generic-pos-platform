using Licensing.Contracts;
using Platform.Core.Licensing;
using Platform.Core.Modules;

namespace Client.Licensing.Domain;

/// <summary>Why a stored license was judged <see cref="LicenseState.Invalid"/>.</summary>
public enum InvalidReason
{
    None = 0,
    Malformed = 1,
    UntrustedKey = 2,
    BadSignature = 3,
    WrongInstallation = 4,
    WrongProduct = 5,
    NotYetValid = 6,

    /// <summary>The system clock is earlier than the last time this installation provably ran: it was turned back (or is badly wrong).</summary>
    ClockRollback = 7
}

/// <summary>Why a license is <see cref="LicenseState.Expired"/>.</summary>
public enum ExpiryKind
{
    None = 0,

    /// <summary>The commercial validity (ValidUntil) has passed.</summary>
    LicenseExpired = 1,

    /// <summary>The offline lease AND its grace period have passed without renewal.</summary>
    LeaseExpired = 2
}

/// <summary>Configurable mechanism parameters. Commercial values are NOT defined here.</summary>
/// <param name="GraceGrantsEntitlements">
/// When true (default) entitlements remain granted during <see cref="LicenseState.GracePeriod"/> (a warning phase).
/// When false, grace behaves like a restricted state.
/// </param>
public sealed record LicensePolicy(bool GraceGrantsEntitlements = true);

/// <summary>Result of verifying the signature/format of a stored or received license. Payload is only set when valid.</summary>
public sealed record LicenseVerificationResult(bool IsValid, LicensePayload? Payload, InvalidReason Reason)
{
    public static LicenseVerificationResult Valid(LicensePayload payload) => new(true, payload, InvalidReason.None);
    public static LicenseVerificationResult Invalid(InvalidReason reason) => new(false, null, reason);
}

/// <summary>The outcome of evaluating a license at a point in time. Immutable.</summary>
public sealed record LicenseEvaluation(
    LicenseState State,
    LicensePayload? Payload,
    InvalidReason InvalidReason,
    ExpiryKind ExpiryKind,
    bool GrantsEntitlements,
    DateTimeOffset EvaluatedAt)
{
    public bool IsModuleLicensed(ModuleId moduleId)
        => GrantsEntitlements && Payload is not null && Contains(Payload.Modules, moduleId.Value);

    public bool IsFeatureLicensed(FeatureId featureId)
        => GrantsEntitlements && Payload is not null && Contains(Payload.Features, featureId.Value);

    private static bool Contains(IReadOnlyList<string> values, string id)
        => values.Any(v => string.Equals(v?.Trim(), id, StringComparison.OrdinalIgnoreCase));

    public static LicenseEvaluation Unlicensed(DateTimeOffset now)
        => new(LicenseState.Unlicensed, null, InvalidReason.None, ExpiryKind.None, false, now);
}

/// <summary>
/// Deterministic, pure license state evaluation. No I/O, no clock, no network: everything is a parameter.
///
/// Order of decisions (first match wins):
///   1. no license                              -> Unlicensed
///   2. signature/format invalid                -> Invalid (reason)
///   3. bound to another installation / product -> Invalid (WrongInstallation / WrongProduct)
///   4. signed status Revoked / Suspended       -> Revoked / Suspended
///   5. now &lt; ValidFrom                      -> Invalid (NotYetValid)
///   6. now &gt; ValidUntil                     -> Expired (LicenseExpired)
///   7. now &lt;= LeaseValidUntil               -> Active
///   8. now &lt;= GracePeriodUntil              -> GracePeriod
///   9. otherwise                               -> Expired (LeaseExpired)
/// All end instants are INCLUSIVE (a license is still valid at exactly its end instant).
/// </summary>
public static class LicenseEvaluator
{
    public static LicenseEvaluation Evaluate(
        bool hasLicense,
        LicenseVerificationResult? verification,
        Guid installationId,
        string expectedProductId,
        DateTimeOffset now,
        LicensePolicy policy)
    {
        if (!hasLicense)
            return LicenseEvaluation.Unlicensed(now);

        if (verification is null || !verification.IsValid || verification.Payload is null)
            return Make(LicenseState.Invalid, null, now, policy,
                verification?.Reason is null or InvalidReason.None ? InvalidReason.Malformed : verification.Reason);

        var p = verification.Payload;

        if (p.InstallationId != installationId)
            return Make(LicenseState.Invalid, null, now, policy, InvalidReason.WrongInstallation);

        if (!string.Equals(p.ProductId, expectedProductId, StringComparison.OrdinalIgnoreCase))
            return Make(LicenseState.Invalid, null, now, policy, InvalidReason.WrongProduct);

        if (p.Status == LicenseStatusClaim.Revoked)
            return Make(LicenseState.Revoked, p, now, policy);

        if (p.Status == LicenseStatusClaim.Suspended)
            return Make(LicenseState.Suspended, p, now, policy);

        if (now < p.ValidFrom)
            return Make(LicenseState.Invalid, null, now, policy, InvalidReason.NotYetValid);

        if (now > p.ValidUntil)
            return Make(LicenseState.Expired, p, now, policy, expiry: ExpiryKind.LicenseExpired);

        if (now <= p.LeaseValidUntil)
            return Make(LicenseState.Active, p, now, policy);

        if (now <= p.GracePeriodUntil)
            return Make(LicenseState.GracePeriod, p, now, policy);

        return Make(LicenseState.Expired, p, now, policy, expiry: ExpiryKind.LeaseExpired);
    }

    private static LicenseEvaluation Make(
        LicenseState state,
        LicensePayload? payload,
        DateTimeOffset now,
        LicensePolicy policy,
        InvalidReason invalid = InvalidReason.None,
        ExpiryKind expiry = ExpiryKind.None)
    {
        var grants = state switch
        {
            LicenseState.Active => true,
            LicenseState.GracePeriod => policy.GraceGrantsEntitlements,
            _ => false
        };

        return new LicenseEvaluation(state, payload, invalid, expiry, grants, now);
    }
}
