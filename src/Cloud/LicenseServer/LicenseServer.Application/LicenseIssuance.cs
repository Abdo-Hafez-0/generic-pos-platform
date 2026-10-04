using Licensing.Contracts;

namespace LicenseServer.Application;

/// <summary>A license as the server tracks it (the commercial record the signed payloads are derived from).</summary>
public sealed class LicenseRecord
{
    public required Guid LicenseId { get; init; }
    public required string CustomerId { get; init; }
    public required string ActivationKey { get; init; }
    public required string ProductId { get; init; }
    public required DateTimeOffset ValidFrom { get; init; }
    public required DateTimeOffset ValidUntil { get; init; }
    public required IReadOnlyList<string> Modules { get; init; }
    public required IReadOnlyList<string> Features { get; init; }

    /// <summary>Set on first activation; a license is bound to one installation at a time.</summary>
    public Guid? InstallationId { get; set; }

    public LicenseStatusClaim Status { get; set; } = LicenseStatusClaim.Active;

    /// <summary>Last LicenseVersion issued; every issuance increments it.</summary>
    public int Version { get; set; }
}

/// <summary>Server-side license persistence abstraction (the server's own storage; never a client or business DB).</summary>
public interface ILicenseRepository
{
    Task<LicenseRecord?> FindByActivationKeyAsync(string activationKey, CancellationToken cancellationToken = default);
    Task<LicenseRecord?> FindByIdAsync(Guid licenseId, CancellationToken cancellationToken = default);
    Task AddAsync(LicenseRecord record, CancellationToken cancellationToken = default);
    Task SaveAsync(LicenseRecord record, CancellationToken cancellationToken = default);
}

/// <summary>Signs bytes with the server's private key. The private key never leaves the implementation.</summary>
public interface ILicenseSigner
{
    string KeyId { get; }

    string Algorithm { get; }

    /// <summary>Returns the raw signature over <paramref name="data"/> in the format of <see cref="Algorithm"/>.</summary>
    byte[] Sign(byte[] data);
}

/// <summary>Issuance parameters. Commercial values are configuration, not constants.</summary>
/// <param name="LeaseDuration">How long a freshly issued license may be used offline before renewal is needed.</param>
/// <param name="GraceDuration">Grace period that follows the lease.</param>
/// <param name="Issuer">Issuer name written into licenses.</param>
public sealed record LicenseServerOptions(TimeSpan LeaseDuration, TimeSpan GraceDuration, string Issuer);

/// <summary>
/// Activation, renewal and status changes. Produces signed licenses; contains no HTTP and no persistence details.
/// Lease and grace are clamped to the commercial ValidUntil.
/// </summary>
public sealed class LicenseIssuanceService(
    ILicenseRepository repository,
    ILicenseSigner signer,
    TimeProvider timeProvider,
    LicenseServerOptions options)
{
    public async Task<ActivationResponse> ActivateAsync(ActivationRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.ActivationKey)
            || request.InstallationId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.ProductId))
            return ActivationResponse.Failure(LicenseErrorCodes.InvalidRequest, "Activation key, installation ID and product ID are required.");

        var record = await repository.FindByActivationKeyAsync(request.ActivationKey.Trim(), cancellationToken);
        if (record is null)
            return ActivationResponse.Failure(LicenseErrorCodes.NotFound, "Unknown activation key.");

        if (!string.Equals(record.ProductId, request.ProductId, StringComparison.OrdinalIgnoreCase))
            return ActivationResponse.Failure(LicenseErrorCodes.ProductMismatch, "The license is for a different product.");

        if (record.Status == LicenseStatusClaim.Revoked)
            return ActivationResponse.Failure(LicenseErrorCodes.Revoked, "The license has been revoked.");

        if (record.Status == LicenseStatusClaim.Suspended)
            return ActivationResponse.Failure(LicenseErrorCodes.Suspended, "The license is suspended.");

        if (record.InstallationId is { } bound && bound != request.InstallationId)
            return ActivationResponse.Failure(LicenseErrorCodes.AlreadyActivated, "The license is already activated on another installation.");

        record.InstallationId = request.InstallationId;
        return ActivationResponse.Success(await IssueAsync(record, cancellationToken));
    }

    public async Task<RenewalResponse> RenewAsync(RenewalRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || request.LicenseId == Guid.Empty || request.InstallationId == Guid.Empty)
            return RenewalResponse.Failure(LicenseErrorCodes.InvalidRequest, "License ID and installation ID are required.");

        var record = await repository.FindByIdAsync(request.LicenseId, cancellationToken);
        if (record is null)
            return RenewalResponse.Failure(LicenseErrorCodes.NotFound, "Unknown license.");

        if (record.InstallationId != request.InstallationId)
            return RenewalResponse.Failure(LicenseErrorCodes.InstallationMismatch, "The license is not bound to this installation.");

        // Suspended/Revoked licenses are still re-issued (signed) so the client learns the state and can evaluate it offline.
        return RenewalResponse.Success(await IssueAsync(record, cancellationToken));
    }

    /// <summary>Suspends or revokes (or reactivates) a license. Takes effect on the client at its next renewal.</summary>
    public async Task<bool> SetStatusAsync(Guid licenseId, LicenseStatusClaim status, CancellationToken cancellationToken = default)
    {
        var record = await repository.FindByIdAsync(licenseId, cancellationToken);
        if (record is null) return false;

        record.Status = status;
        await repository.SaveAsync(record, cancellationToken);
        return true;
    }

    private async Task<SignedLicense> IssueAsync(LicenseRecord record, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        record.Version++;

        var lease = Min(now + options.LeaseDuration, record.ValidUntil);
        var grace = Min(lease + options.GraceDuration, record.ValidUntil);

        var payload = new LicensePayload(
            LicenseId: record.LicenseId,
            CustomerId: record.CustomerId,
            InstallationId: record.InstallationId!.Value,
            ProductId: record.ProductId,
            LicenseVersion: record.Version,
            IssuedAt: now,
            ValidFrom: record.ValidFrom,
            ValidUntil: record.ValidUntil,
            LeaseValidUntil: lease,
            GracePeriodUntil: grace,
            Status: record.Status,
            Modules: record.Modules,
            Features: record.Features,
            Issuer: options.Issuer,
            KeyId: signer.KeyId);

        var bytes = LicenseSerializer.SerializePayloadBytes(payload);
        var signature = signer.Sign(bytes);

        await repository.SaveAsync(record, cancellationToken);

        return new SignedLicense(
            LicenseSerializer.ToPayloadText(bytes),
            signer.KeyId,
            signer.Algorithm,
            Convert.ToBase64String(signature));
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
}
