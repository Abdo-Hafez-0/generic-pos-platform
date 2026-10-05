using BackupServer.Application;
using Cloud.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using UpdateServer.Application;
using Updates.Contracts;

namespace Cloud.Infrastructure.Repositories;

/// <summary>Durable package metadata. The signed manifest envelope is stored verbatim, exactly as published.</summary>
public sealed class EfPackageCatalog(IDbContextFactory<CloudDbContext> factory) : IPackageCatalog
{
    public async Task<ManagedPackage?> FindAsync(Guid packageId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.PackageId == packageId, cancellationToken);
        return entity is null ? null : PackageMapping.ToModel(entity);
    }

    public async Task<IReadOnlyList<ManagedPackage>> ListAsync(string? targetId = null, PackageStatus? status = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.Packages.AsNoTracking().AsQueryable();
        if (targetId is not null) query = query.Where(p => p.TargetId == targetId);
        if (status is { } s) query = query.Where(p => p.Status == (int)s);

        return (await query.ToListAsync(cancellationToken)).Select(PackageMapping.ToModel).ToList();
    }

    public async Task<bool> VersionExistsAsync(string targetId, string version, string targetFramework, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Packages.AnyAsync(p => p.TargetId == targetId && p.Version == version && p.TargetFramework == targetFramework, cancellationToken);
    }

    public async Task AddAsync(ManagedPackage package, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Packages.Add(PackageMapping.ToEntity(package));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> SetStatusAsync(Guid packageId, PackageStatus status, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        DateTimeOffset? withdrawnAt = status == PackageStatus.Withdrawn ? at : null;

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var changed = await db.Packages.Where(p => p.PackageId == packageId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, (int)status).SetProperty(p => p.WithdrawnAt, withdrawnAt), cancellationToken);
        return changed > 0;
    }
}

internal static class PackageMapping
{
    public static PackageEntity ToEntity(ManagedPackage m) => new()
    {
        PackageId = m.Package.PackageId,
        PackageType = (int)m.Package.PackageType,
        TargetId = m.Package.TargetId,
        Version = m.Package.Version,
        TargetFramework = m.Package.TargetFramework,
        MinimumHostVersion = m.Package.MinimumHostVersion,
        SizeBytes = m.Package.SizeBytes,
        Sha256 = m.Package.Sha256,
        EnvelopeJson = PackageManifestSerializer.SerializeEnvelope(m.Package.SignedManifest),
        KeyId = m.KeyId,
        Publisher = m.Publisher,
        Status = (int)m.Status,
        ReleaseNotes = m.ReleaseNotes,
        PublishedAt = m.PublishedAt,
        PublishedBy = m.PublishedBy,
        WithdrawnAt = m.WithdrawnAt
    };

    public static ManagedPackage ToModel(PackageEntity e)
    {
        var envelope = PackageManifestSerializer.TryParseEnvelope(e.EnvelopeJson)
            ?? throw new InvalidOperationException($"The stored manifest of package {e.PackageId} is unreadable.");

        var package = new PublishedPackage(e.PackageId, (PackageType)e.PackageType, e.TargetId, e.Version, e.TargetFramework,
            e.MinimumHostVersion, envelope, e.SizeBytes, e.Sha256);

        return new ManagedPackage(package, e.KeyId, e.Publisher, (PackageStatus)e.Status, e.ReleaseNotes, e.PublishedAt, e.PublishedBy, e.WithdrawnAt);
    }
}

/// <summary>
/// The discovery-facing view of the durable catalog: <see cref="IPackageRepository"/> as the Stage 7 update server expects it,
/// serving only PUBLISHED packages (withdrawn ones are neither offered nor downloadable).
/// </summary>
public sealed class CatalogPackageRepository(IDbContextFactory<CloudDbContext> factory, IPackageFileStore files) : IPackageRepository
{
    public IReadOnlyList<PublishedPackage> List()
    {
        using var db = factory.CreateDbContext();
        return db.Packages.AsNoTracking().Where(p => p.Status == (int)PackageStatus.Published)
            .ToList().Select(p => PackageMapping.ToModel(p).Package).ToList();
    }

    public PublishedPackage? Find(Guid packageId)
    {
        using var db = factory.CreateDbContext();
        var entity = db.Packages.AsNoTracking().FirstOrDefault(p => p.PackageId == packageId && p.Status == (int)PackageStatus.Published);
        return entity is null ? null : PackageMapping.ToModel(entity).Package;
    }

    public Stream? OpenRead(Guid packageId) => Find(packageId) is null ? null : files.OpenRead(packageId);
}

public sealed class EfBackupCatalog(IDbContextFactory<CloudDbContext> factory) : IBackupCatalog
{
    public async Task AddAsync(BackupRecord record, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Backups.Add(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<BackupRecord?> FindAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Backups.AsNoTracking().FirstOrDefaultAsync(b => b.BackupId == backupId, cancellationToken);
    }

    public async Task<IReadOnlyList<BackupRecord>> ListForLicenseAsync(Guid licenseId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Backups.AsNoTracking().Where(b => b.LicenseId == licenseId)
            .OrderByDescending(b => b.CreatedAt).ThenBy(b => b.BackupId).ToListAsync(cancellationToken);
    }

    public async Task<BackupPage> ListAsync(Guid? licenseId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.Backups.AsNoTracking().Where(b => licenseId == null || b.LicenseId == licenseId);

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(b => b.CreatedAt).ThenBy(b => b.BackupId)
            .Skip((Math.Max(page, 1) - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new BackupPage(items, total);
    }

    public async Task<bool> DeleteAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Backups.Where(b => b.BackupId == backupId).ExecuteDeleteAsync(cancellationToken) > 0;
    }

    public async Task<BackupUsage> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var count = await db.Backups.CountAsync(cancellationToken);
        var bytes = count == 0 ? 0 : await db.Backups.SumAsync(b => b.SizeBytes, cancellationToken);
        return new BackupUsage(count, bytes);
    }
}

public sealed class EfBackupAccessTokenStore(IDbContextFactory<CloudDbContext> factory) : IBackupAccessTokenStore
{
    public async Task SetAsync(Guid licenseId, string tokenHash, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.BackupTokens.Where(t => t.LicenseId == licenseId).ExecuteDeleteAsync(cancellationToken);
        db.BackupTokens.Add(new BackupTokenEntity { LicenseId = licenseId, TokenHash = tokenHash, CreatedAt = createdAt });
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<Guid?> FindLicenseAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.BackupTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
        return entity?.LicenseId;
    }

    public async Task<bool> RemoveAsync(Guid licenseId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.BackupTokens.Where(t => t.LicenseId == licenseId).ExecuteDeleteAsync(cancellationToken) > 0;
    }

    public async Task<bool> ExistsAsync(Guid licenseId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.BackupTokens.AnyAsync(t => t.LicenseId == licenseId, cancellationToken);
    }
}
