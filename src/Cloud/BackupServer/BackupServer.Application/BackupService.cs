using System.Security.Cryptography;
using System.Text;
using Cloud.Contracts;

namespace BackupServer.Application;

/// <summary>Generation and storage form of backup access tokens (only the hash is stored).</summary>
public static class BackupTokens
{
    public const string Prefix = "gpb_";

    public static string Generate()
        => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()))).ToLowerInvariant();
}

/// <summary>What the client tells the server about an upload (all optional and informational, except the hash check).</summary>
public sealed record BackupUploadInfo(string? ClaimedSha256, string? Label, string? ClientVersion);

/// <summary>
/// Accepts, lists, serves and deletes opaque backups for authenticated licenses. Business rules only: no HTTP, no EF.
/// Uploads are streamed, size-limited and hashed on the way in; a claimed hash that does not match is rejected; the oldest
/// backups beyond the retention count are removed after a successful upload.
/// </summary>
public sealed class BackupService(
    IBackupCatalog catalog,
    IBackupBlobStore blobs,
    TimeProvider timeProvider,
    BackupServerOptions options)
{
    public const int MaxLabelLength = 200;

    public async Task<ServiceResult<BackupRecord>> UploadAsync(
        BackupPrincipal principal, Stream content, BackupUploadInfo info, CancellationToken cancellationToken = default)
    {
        if (!principal.CanUpload)
            return ServiceResult<BackupRecord>.Fail(CloudErrorCodes.Forbidden, principal.UploadDeniedReason!);

        var label = string.IsNullOrWhiteSpace(info.Label) ? null : info.Label.Trim();
        if (label is { Length: > MaxLabelLength })
            return ServiceResult<BackupRecord>.Fail(CloudErrorCodes.Validation, $"The label may not exceed {MaxLabelLength} characters.");

        var claimed = string.IsNullOrWhiteSpace(info.ClaimedSha256) ? null : info.ClaimedSha256.Trim().ToLowerInvariant();
        if (claimed is not null && (claimed.Length != 64 || !claimed.All(Uri.IsHexDigit)))
            return ServiceResult<BackupRecord>.Fail(CloudErrorCodes.Validation, "The supplied SHA-256 is not a 64-character hex string.");

        var staged = await blobs.StageAsync(content, options.MaxBackupBytes, cancellationToken);
        if (staged is null)
            return ServiceResult<BackupRecord>.Fail(CloudErrorCodes.TooLarge, $"A backup may not exceed {options.MaxBackupBytes} bytes.");

        if (staged.SizeBytes == 0)
        {
            blobs.Discard(staged);
            return ServiceResult<BackupRecord>.Fail(CloudErrorCodes.Validation, "The backup is empty.");
        }

        if (claimed is not null && !string.Equals(claimed, staged.Sha256, StringComparison.Ordinal))
        {
            blobs.Discard(staged);
            return ServiceResult<BackupRecord>.Fail(CloudErrorCodes.BackupHashMismatch, "The uploaded bytes do not match the supplied SHA-256.");
        }

        var record = new BackupRecord
        {
            BackupId = Guid.NewGuid(),
            LicenseId = principal.LicenseId,
            CustomerId = principal.CustomerId,
            InstallationId = principal.InstallationId,
            CreatedAt = timeProvider.GetUtcNow(),
            SizeBytes = staged.SizeBytes,
            Sha256 = staged.Sha256,
            Label = label,
            ClientVersion = string.IsNullOrWhiteSpace(info.ClientVersion) ? null : info.ClientVersion.Trim()
        };

        try
        {
            blobs.Commit(staged, record.BackupId);
        }
        catch
        {
            blobs.Discard(staged);
            throw;
        }

        try
        {
            await catalog.AddAsync(record, cancellationToken);
        }
        catch
        {
            blobs.Delete(record.BackupId);
            throw;
        }

        await PruneAsync(principal.LicenseId, cancellationToken);
        return ServiceResult<BackupRecord>.Ok(record);
    }

    public Task<IReadOnlyList<BackupRecord>> ListAsync(BackupPrincipal principal, CancellationToken cancellationToken = default)
        => catalog.ListForLicenseAsync(principal.LicenseId, cancellationToken);

    public async Task<ServiceResult<BackupRecord>> FindAsync(BackupPrincipal principal, Guid backupId, CancellationToken cancellationToken = default)
    {
        var record = await catalog.FindAsync(backupId, cancellationToken);
        return record is null || record.LicenseId != principal.LicenseId
            ? ServiceResult<BackupRecord>.Fail(CloudErrorCodes.NotFound, "Unknown backup.")
            : ServiceResult<BackupRecord>.Ok(record);
    }

    /// <summary>Opens the content of one of the caller's own backups. The caller disposes the stream.</summary>
    public async Task<ServiceResult<Stream>> OpenContentAsync(BackupPrincipal principal, Guid backupId, CancellationToken cancellationToken = default)
    {
        var found = await FindAsync(principal, backupId, cancellationToken);
        if (!found.IsSuccess)
            return ServiceResult<Stream>.From(found.Error!);

        var stream = blobs.OpenRead(backupId);
        return stream is null
            ? ServiceResult<Stream>.Fail(CloudErrorCodes.NotFound, "The backup content is no longer available.")
            : ServiceResult<Stream>.Ok(stream);
    }

    public async Task<ServiceResult> DeleteAsync(BackupPrincipal principal, Guid backupId, CancellationToken cancellationToken = default)
    {
        var found = await FindAsync(principal, backupId, cancellationToken);
        if (!found.IsSuccess)
            return ServiceResult.Fail(found.Error!.Code, found.Error.Message);

        await RemoveAsync(backupId, cancellationToken);
        return ServiceResult.Ok();
    }

    // ---- administration (no principal: the vendor's view) ---------------------------------------------------------

    public Task<BackupPage> ListAllAsync(Guid? licenseId, int page, int pageSize, CancellationToken cancellationToken = default)
        => catalog.ListAsync(licenseId, page, pageSize, cancellationToken);

    public async Task<bool> DeleteAnyAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        if (await catalog.FindAsync(backupId, cancellationToken) is null)
            return false;

        await RemoveAsync(backupId, cancellationToken);
        return true;
    }

    public Task<BackupUsage> GetUsageAsync(CancellationToken cancellationToken = default)
        => catalog.GetUsageAsync(cancellationToken);

    private async Task RemoveAsync(Guid backupId, CancellationToken cancellationToken)
    {
        // Catalog first: an orphaned file is harmless, a catalog row pointing at nothing is not.
        await catalog.DeleteAsync(backupId, cancellationToken);
        blobs.Delete(backupId);
    }

    private async Task PruneAsync(Guid licenseId, CancellationToken cancellationToken)
    {
        if (options.MaxBackupsPerLicense <= 0) return;

        var all = await catalog.ListForLicenseAsync(licenseId, cancellationToken);
        foreach (var old in all.Skip(options.MaxBackupsPerLicense))
            await RemoveAsync(old.BackupId, cancellationToken);
    }
}
