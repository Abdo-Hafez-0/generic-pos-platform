using Cloud.Contracts;
using LicenseServer.Application;
using Licensing.Contracts;

namespace BackupServer.Application;

/// <summary>Metadata of one stored backup. The content is opaque: the server never decrypts, parses or restores it.</summary>
public sealed class BackupRecord
{
    public Guid BackupId { get; set; }
    public Guid LicenseId { get; set; }
    public string CustomerId { get; set; } = "";
    public Guid InstallationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string? Label { get; set; }
    public string? ClientVersion { get; set; }
}

public sealed record BackupPage(IReadOnlyList<BackupRecord> Items, int Total);

public sealed record BackupUsage(int Count, long Bytes);

/// <summary>Backup metadata persistence (the server's own storage).</summary>
public interface IBackupCatalog
{
    Task AddAsync(BackupRecord record, CancellationToken cancellationToken = default);

    Task<BackupRecord?> FindAsync(Guid backupId, CancellationToken cancellationToken = default);

    /// <summary>All backups of a license, newest first.</summary>
    Task<IReadOnlyList<BackupRecord>> ListForLicenseAsync(Guid licenseId, CancellationToken cancellationToken = default);

    Task<BackupPage> ListAsync(Guid? licenseId, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Returns false when the backup is unknown.</summary>
    Task<bool> DeleteAsync(Guid backupId, CancellationToken cancellationToken = default);

    Task<BackupUsage> GetUsageAsync(CancellationToken cancellationToken = default);
}

/// <summary>An upload waiting in the staging area, already measured and hashed.</summary>
public sealed record StagedBlob(string Path, long SizeBytes, string Sha256);

/// <summary>Backup bytes: streamed to staging (hashed while written), promoted, read, deleted.</summary>
public interface IBackupBlobStore
{
    /// <summary>Streams <paramref name="input"/> to staging; returns null (storing nothing) when it exceeds <paramref name="maxBytes"/>.</summary>
    Task<StagedBlob?> StageAsync(Stream input, long maxBytes, CancellationToken cancellationToken = default);

    void Commit(StagedBlob staged, Guid backupId);

    void Discard(StagedBlob staged);

    Stream? OpenRead(Guid backupId);

    void Delete(Guid backupId);
}

/// <summary>Per-license backup access tokens. Only hashes are stored.</summary>
public interface IBackupAccessTokenStore
{
    /// <summary>Sets (replacing any previous) the token hash of a license.</summary>
    Task SetAsync(Guid licenseId, string tokenHash, DateTimeOffset createdAt, CancellationToken cancellationToken = default);

    Task<Guid?> FindLicenseAsync(string tokenHash, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(Guid licenseId, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(Guid licenseId, CancellationToken cancellationToken = default);
}

/// <summary>Backup policy. Values are configuration, not constants.</summary>
/// <param name="MaxBackupBytes">Largest accepted upload.</param>
/// <param name="MaxBackupsPerLicense">Retention: after an upload the oldest backups beyond this count are deleted.</param>
/// <param name="RequiredModule">Module entitlement a license needs to UPLOAD (null/empty = none).</param>
public sealed record BackupServerOptions(long MaxBackupBytes, int MaxBackupsPerLicense, string? RequiredModule)
{
    public const long DefaultMaxBackupBytes = 256L * 1024 * 1024;
    public const int DefaultMaxBackupsPerLicense = 10;
    public const string DefaultRequiredModule = "cloud-backup";

    public static BackupServerOptions Default { get; } = new(DefaultMaxBackupBytes, DefaultMaxBackupsPerLicense, DefaultRequiredModule);
}

/// <summary>An authenticated backup caller: the license behind a valid access token.</summary>
/// <param name="UploadDeniedReason">Null when the license may upload; otherwise why not (reading/restoring stays allowed).</param>
public sealed record BackupPrincipal(Guid LicenseId, string CustomerId, Guid InstallationId, string? UploadDeniedReason)
{
    public bool CanUpload => UploadDeniedReason is null;
}

/// <summary>Issues, revokes and verifies backup access tokens. Never reveals a stored token (only hashes exist).</summary>
public sealed class BackupAccessService(
    IBackupAccessTokenStore tokens,
    ILicenseRepository licenses,
    TimeProvider timeProvider,
    BackupServerOptions options)
{
    /// <summary>Creates (or rotates) the license's token. The plaintext is returned ONCE.</summary>
    public async Task<ServiceResult<string>> IssueTokenAsync(Guid licenseId, CancellationToken cancellationToken = default)
    {
        var license = await licenses.FindByIdAsync(licenseId, cancellationToken);
        if (license is null)
            return ServiceResult<string>.Fail(CloudErrorCodes.NotFound, "Unknown license.");

        if (license.Status == LicenseStatusClaim.Revoked)
            return ServiceResult<string>.Fail(CloudErrorCodes.InvalidState, "A revoked license cannot receive backup access.");

        var token = BackupTokens.Generate();
        await tokens.SetAsync(licenseId, BackupTokens.Hash(token), timeProvider.GetUtcNow(), cancellationToken);
        return ServiceResult<string>.Ok(token);
    }

    public Task<bool> RevokeTokenAsync(Guid licenseId, CancellationToken cancellationToken = default)
        => tokens.RemoveAsync(licenseId, cancellationToken);

    public Task<bool> HasTokenAsync(Guid licenseId, CancellationToken cancellationToken = default)
        => tokens.ExistsAsync(licenseId, cancellationToken);

    public async Task<ServiceResult<BackupPrincipal>> AuthenticateAsync(string? token, CancellationToken cancellationToken = default)
    {
        const string rejected = "The backup access token is missing or invalid.";

        if (string.IsNullOrWhiteSpace(token))
            return ServiceResult<BackupPrincipal>.Fail(CloudErrorCodes.Unauthorized, rejected);

        var licenseId = await tokens.FindLicenseAsync(BackupTokens.Hash(token), cancellationToken);
        var license = licenseId is null ? null : await licenses.FindByIdAsync(licenseId.Value, cancellationToken);
        if (license is null)
            return ServiceResult<BackupPrincipal>.Fail(CloudErrorCodes.Unauthorized, rejected);

        // A revoked license loses cloud access; an expired or suspended one may still READ and RESTORE its own backups.
        if (license.Status == LicenseStatusClaim.Revoked)
            return ServiceResult<BackupPrincipal>.Fail(CloudErrorCodes.Forbidden, "The license has been revoked.");

        if (license.InstallationId is not { } installation)
            return ServiceResult<BackupPrincipal>.Fail(CloudErrorCodes.Forbidden, "The license has not been activated on an installation.");

        return ServiceResult<BackupPrincipal>.Ok(
            new BackupPrincipal(license.LicenseId, license.CustomerId, installation, UploadDenial(license)));
    }

    private string? UploadDenial(LicenseRecord license)
    {
        if (license.Status != LicenseStatusClaim.Active)
            return "The license is not active.";

        if (timeProvider.GetUtcNow() > license.ValidUntil)
            return "The license has expired.";

        if (!string.IsNullOrWhiteSpace(options.RequiredModule)
            && !license.Modules.Contains(options.RequiredModule, StringComparer.OrdinalIgnoreCase))
            return $"The license does not include the '{options.RequiredModule}' module.";

        return null;
    }
}
