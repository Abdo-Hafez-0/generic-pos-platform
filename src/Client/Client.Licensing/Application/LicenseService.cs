using Client.Licensing.Domain;
using Licensing.Contracts;
using Platform.Application.Abstractions.Licensing;
using Platform.Application.Abstractions.Security;
using Platform.Core.Licensing;
using Platform.Core.Modules;
using Platform.Core.Results;

namespace Client.Licensing.Application;

/// <summary>The client-side licensing use cases.</summary>
public interface ILicenseService
{
    /// <summary>Loads the identity and stored license and verifies it. Offline; never throws for a bad/missing license.</summary>
    Task<LicenseEvaluation> InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Evaluates the already-verified license against the current time. Offline and synchronous.</summary>
    LicenseEvaluation Current { get; }

    Task<InstallationIdentity> GetInstallationIdentityAsync(CancellationToken cancellationToken = default);

    /// <summary>Activates with the server, verifies the signed response, then stores it.</summary>
    Task<Result<LicenseEvaluation>> ActivateAsync(string activationKey, CancellationToken cancellationToken = default);

    /// <summary>Asks the server for a fresh signed license/lease; verifies it; stores it only if it is valid and newer.</summary>
    Task<Result<LicenseEvaluation>> RenewAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Orchestrates identity, local storage, signature verification, server communication and evaluation.
///
/// Guarantees:
///  - Evaluation (<see cref="Current"/>, entitlement queries) never touches the network or disk: it re-runs the pure
///    <see cref="LicenseEvaluator"/> on the cached, already-verified license with the current time.
///  - A server response is never trusted on its own: it must verify against a trusted public key and be bound to this
///    installation/product before it is stored or becomes current.
///  - Failures (unreachable server, bad response) never replace or delete the existing local license.
///  - This service has no access to business data and never modifies it.
/// No static state: one instance holds an immutable snapshot, swapped atomically.
/// </summary>
public sealed class LicenseService(
    InstallationIdentityService identityService,
    ILicenseStore store,
    ILicenseVerifier verifier,
    ILicenseClient client,
    TimeProvider timeProvider,
    LicensingOptions options,
    LicensePolicy policy,
    ISecurityEventSink? events = null,
    IClockGuard? clockGuard = null) : ILicenseService, ILicenseEntitlementService
{
    private sealed record Snapshot(Guid InstallationId, bool HasLicense, LicenseVerificationResult? Verification);

    private Snapshot? _snapshot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _rollbackReported;

    public async Task<InstallationIdentity> GetInstallationIdentityAsync(CancellationToken cancellationToken = default)
        => await identityService.GetOrCreateAsync(cancellationToken);

    public async Task<LicenseEvaluation> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (clockGuard is not null) await clockGuard.LoadAsync(cancellationToken);
        var identity = await identityService.GetOrCreateAsync(cancellationToken);
        var stored = await store.LoadAsync(cancellationToken);

        Snapshot snapshot;
        if (stored is null)
            snapshot = new Snapshot(identity.InstallationId, false, null);
        else if (stored.License is null)
            snapshot = new Snapshot(identity.InstallationId, true, LicenseVerificationResult.Invalid(InvalidReason.Malformed));
        else
            snapshot = new Snapshot(identity.InstallationId, true, verifier.Verify(stored.License));

        // The signed issue time of the stored license is a fact nobody on this machine can move: the clock may never be earlier than it.
        if (clockGuard is not null && snapshot.Verification is { IsValid: true, Payload: { } stored2 })
            clockGuard.SetFloor(stored2.IssuedAt);

        Volatile.Write(ref _snapshot, snapshot);
        var evaluation = Current;
        await Record(
            evaluation.State is LicenseState.Active or LicenseState.GracePeriod or LicenseState.Unlicensed ? SecurityEventOutcome.Success : SecurityEventOutcome.Denied,
            "security.license.loaded", evaluation.Payload?.LicenseId,
            evaluation.State == LicenseState.Invalid ? $"{evaluation.State}: {evaluation.InvalidReason}" : evaluation.State.ToString(), cancellationToken);
        return evaluation;
    }

    /// <summary>Records a licensing security event (never a key, token or the license content): the state, the license ID, the reason.</summary>
    private Task Record(SecurityEventOutcome outcome, string action, Guid? licenseId, string summary, CancellationToken cancellationToken)
        => events.TryRecordAsync(SecurityEvent.Create(
            action, outcome, subjectType: "license", subjectId: licenseId?.ToString(), summary: summary, occurredAt: timeProvider.GetUtcNow()), cancellationToken);

    public LicenseEvaluation Current
    {
        get
        {
            var snapshot = Volatile.Read(ref _snapshot);
            var now = timeProvider.GetUtcNow();
            if (snapshot is null)
                return LicenseEvaluation.Unlicensed(now);

            // A clock that cannot be believed grants nothing: expiry means nothing if the clock can simply be turned back.
            if (clockGuard is not null && snapshot.HasLicense)
            {
                if (clockGuard.Observe(now) == ClockCheck.RolledBack)
                {
                    if (Interlocked.Exchange(ref _rollbackReported, 1) == 0)
                        _ = Record(SecurityEventOutcome.Denied, "security.license.clock-rollback", snapshot.Verification?.Payload?.LicenseId,
                            "the system clock is earlier than the last time this installation ran; licensed work is restricted until the clock is corrected or the license is renewed online",
                            CancellationToken.None);

                    return new LicenseEvaluation(LicenseState.Invalid, null, InvalidReason.ClockRollback, ExpiryKind.None, false, now);
                }

                Interlocked.Exchange(ref _rollbackReported, 0);
            }

            return LicenseEvaluator.Evaluate(
                snapshot.HasLicense, snapshot.Verification, snapshot.InstallationId, options.ProductId, now, policy);
        }
    }

    // ILicenseEntitlementService ------------------------------------------------

    public LicenseState State => Current.State;

    public bool IsModuleLicensed(ModuleId moduleId) => Current.IsModuleLicensed(moduleId);

    public bool IsFeatureLicensed(FeatureId featureId) => Current.IsFeatureLicensed(featureId);

    // Activation / renewal --------------------------------------------------------

    public async Task<Result<LicenseEvaluation>> ActivateAsync(string activationKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(activationKey))
            return Result.Failure<LicenseEvaluation>(Error.Validation(
                "Licensing.Activation.KeyRequired", "An activation key is required."));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var identity = await identityService.GetOrCreateAsync(cancellationToken);

            ActivationResponse response;
            try
            {
                response = await client.ActivateAsync(
                    new ActivationRequest(activationKey.Trim(), identity.InstallationId, options.ProductId), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Result.Failure<LicenseEvaluation>(Error.Failure(
                    LicenseErrorCodes.ServerUnreachable, "The license server could not be reached. Local operation is not affected; try again when the connection is back."));
            }

            if (!response.IsSuccess || response.License is null)
            {
                await Record(SecurityEventOutcome.Failure, "security.license.activation-failed", null,
                    response.ErrorCode ?? LicenseErrorCodes.ServerRejected, cancellationToken);
                return Result.Failure<LicenseEvaluation>(Error.Failure(
                    response.ErrorCode ?? LicenseErrorCodes.ServerRejected,
                    response.ErrorMessage ?? "The license server rejected the activation."));
            }

            var accepted = await VerifyAndAcceptAsync(response.License, identity.InstallationId, current: null, cancellationToken);
            return accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<LicenseEvaluation>> RenewAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var identity = await identityService.GetOrCreateAsync(cancellationToken);
            var snapshot = Volatile.Read(ref _snapshot);
            var currentPayload = snapshot?.Verification is { IsValid: true } v ? v.Payload : null;

            // Only a license that passed signature verification can be renewed (its LicenseId is trusted).
            if (currentPayload is null)
                return Result.Failure<LicenseEvaluation>(Error.Conflict(
                    "Licensing.Renewal.NoValidLicense", "There is no verified license to renew. Activate first."));

            RenewalResponse response;
            try
            {
                response = await client.RenewAsync(
                    new RenewalRequest(currentPayload.LicenseId, identity.InstallationId, currentPayload.LicenseVersion), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Result.Failure<LicenseEvaluation>(Error.Failure(
                    LicenseErrorCodes.ServerUnreachable, "The license server could not be reached. Local operation is not affected; try again when the connection is back."));
            }

            if (!response.IsSuccess || response.License is null)
                return Result.Failure<LicenseEvaluation>(Error.Failure(
                    response.ErrorCode ?? LicenseErrorCodes.ServerRejected,
                    response.ErrorMessage ?? "The license server rejected the renewal."));

            return await VerifyAndAcceptAsync(response.License, identity.InstallationId, currentPayload, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Verifies a license received from the server. Only if it is cryptographically valid, bound to this installation
    /// and product, (for renewals) the same license and a newer version, is it stored and made current.
    /// </summary>
    private async Task<Result<LicenseEvaluation>> VerifyAndAcceptAsync(
        SignedLicense received,
        Guid installationId,
        LicensePayload? current,
        CancellationToken cancellationToken)
    {
        var verification = verifier.Verify(received);
        if (!verification.IsValid || verification.Payload is null)
        {
            await Record(SecurityEventOutcome.Denied, "security.license.rejected", null, $"failed verification: {verification.Reason}", cancellationToken);
            return Result.Failure<LicenseEvaluation>(Error.Validation(
                "Licensing.Verification.Failed",
                $"The license received from the server failed verification ({verification.Reason}). It was not stored."));
        }

        var evaluation = LicenseEvaluator.Evaluate(
            true, verification, installationId, options.ProductId, timeProvider.GetUtcNow(), policy);

        if (evaluation.State == LicenseState.Invalid)
        {
            await Record(SecurityEventOutcome.Denied, "security.license.rejected", verification.Payload.LicenseId,
                $"not valid for this installation: {evaluation.InvalidReason}", cancellationToken);
            return Result.Failure<LicenseEvaluation>(Error.Validation(
                "Licensing.Verification.Rejected",
                $"The license received from the server is not valid for this installation ({evaluation.InvalidReason}). It was not stored."));
        }

        if (current is not null)
        {
            var payload = verification.Payload;
            if (payload.LicenseId != current.LicenseId)
            {
                await Record(SecurityEventOutcome.Denied, "security.license.rejected", payload.LicenseId, "renewal is a different license", cancellationToken);
                return Result.Failure<LicenseEvaluation>(Error.Validation(
                    "Licensing.Renewal.WrongLicense", "The renewed license is a different license. It was not stored."));
            }

            if (payload.LicenseVersion <= current.LicenseVersion)
            {
                await Record(SecurityEventOutcome.Denied, "security.license.rejected", payload.LicenseId, "renewal is not newer than the current license (replay)", cancellationToken);
                return Result.Failure<LicenseEvaluation>(Error.Validation(
                    "Licensing.Renewal.Stale", "The renewed license is not newer than the current one. It was not stored."));
            }
        }

        await store.SaveAsync(received, cancellationToken);
        Volatile.Write(ref _snapshot, new Snapshot(installationId, true, verification));
        if (clockGuard is not null)
        {
            // The server just vouched for the time of issue: the mark follows it (also the way out of a clock that had run far ahead).
            clockGuard.Rebase(verification.Payload.IssuedAt);
            await clockGuard.FlushAsync(cancellationToken);
        }

        await Record(SecurityEventOutcome.Success, current is null ? "security.license.activated" : "security.license.renewed",
            verification.Payload.LicenseId, $"version {verification.Payload.LicenseVersion}, state {Current.State}", cancellationToken);
        return Result.Success(Current);
    }
}
