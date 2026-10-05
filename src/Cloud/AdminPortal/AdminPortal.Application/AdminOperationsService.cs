using BackupServer.Application;
using Cloud.Contracts;
using Cloud.Contracts.Admin;
using Cloud.Contracts.Backup;
using LicenseServer.Application;
using Licensing.Contracts;
using UpdateServer.Application;

namespace AdminPortal.Application;

/// <summary>Cross-cutting administration: the dashboard, the audit trail and the vendor's view of stored backups.</summary>
public sealed class AdminOperationsService(
    ICustomerRepository customers,
    ILicenseQuery licenses,
    IModuleRegistryRepository registry,
    IPackageCatalog packages,
    BackupService backups,
    IAdminAuditLog auditLog,
    AdminAuditRecorder audit)
{
    public async Task<DashboardDto> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var allPackages = await packages.ListAsync(null, null, cancellationToken);
        var usage = await backups.GetUsageAsync(cancellationToken);

        return new DashboardDto(
            Customers: await customers.CountAsync(null, cancellationToken),
            ActiveCustomers: await customers.CountAsync(true, cancellationToken),
            Licenses: await licenses.CountAsync(new LicenseFilter(), cancellationToken),
            ActiveLicenses: await licenses.CountAsync(new LicenseFilter(Status: LicenseStatusClaim.Active), cancellationToken),
            SuspendedLicenses: await licenses.CountAsync(new LicenseFilter(Status: LicenseStatusClaim.Suspended), cancellationToken),
            RevokedLicenses: await licenses.CountAsync(new LicenseFilter(Status: LicenseStatusClaim.Revoked), cancellationToken),
            Installations: await licenses.CountAsync(new LicenseFilter(Bound: true), cancellationToken),
            Modules: (await registry.ListAsync(true, cancellationToken)).Count,
            PublishedPackages: allPackages.Count(p => p.Status == PackageStatus.Published),
            WithdrawnPackages: allPackages.Count(p => p.Status == PackageStatus.Withdrawn),
            Backups: usage.Count,
            BackupBytes: usage.Bytes);
    }

    public async Task<PagedResult<AuditEntryDto>> QueryAuditAsync(AuditFilter filter, int? page, int? pageSize, CancellationToken cancellationToken = default)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var result = await auditLog.QueryAsync(filter, p, size, cancellationToken);

        return new PagedResult<AuditEntryDto>(
            result.Items.Select(e => new AuditEntryDto(e.Id, e.At, e.Actor, e.Action, e.EntityType, e.EntityId, e.Summary)).ToList(), p, size, result.Total);
    }

    public async Task<PagedResult<BackupDto>> ListBackupsAsync(Guid? licenseId, int? page, int? pageSize, CancellationToken cancellationToken = default)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var result = await backups.ListAllAsync(licenseId, p, size, cancellationToken);

        return new PagedResult<BackupDto>(result.Items.Select(ToDto).ToList(), p, size, result.Total);
    }

    public async Task<ServiceResult> DeleteBackupAsync(AdminActor actor, Guid backupId, CancellationToken cancellationToken = default)
    {
        if (!await backups.DeleteAnyAsync(backupId, cancellationToken))
            return ServiceResult.Fail(CloudErrorCodes.NotFound, "Unknown backup.");

        await audit.RecordAsync(actor, "backup.delete", "backup", backupId.ToString(), "Deleted a stored backup.", cancellationToken);
        return ServiceResult.Ok();
    }

    public static BackupDto ToDto(BackupRecord r)
        => new(r.BackupId, r.LicenseId, r.CustomerId, r.InstallationId, r.CreatedAt, r.SizeBytes, r.Sha256, r.Label, r.ClientVersion);
}
