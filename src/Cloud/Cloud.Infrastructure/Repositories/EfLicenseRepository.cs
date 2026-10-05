using System.Text.Json;
using Cloud.Infrastructure.Persistence;
using LicenseServer.Application;
using Licensing.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Cloud.Infrastructure.Repositories;

/// <summary>
/// Durable <see cref="ILicenseRepository"/> over the server database. Activation keys are stored only as SHA-256 hashes, and
/// saves are optimistic (a stale record is never written over a newer one: no lost revocations).
/// </summary>
public sealed class EfLicenseRepository(IDbContextFactory<CloudDbContext> factory) : ILicenseRepository, ILicenseQuery
{
    public async Task<LicenseRecord?> FindByActivationKeyAsync(string activationKey, CancellationToken cancellationToken = default)
    {
        var hash = ActivationKeys.Hash(activationKey);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Licenses.AsNoTracking().FirstOrDefaultAsync(l => l.ActivationKeyHash == hash, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<LicenseRecord?> FindByIdAsync(Guid licenseId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Licenses.AsNoTracking().FirstOrDefaultAsync(l => l.Id == licenseId, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    /// <summary>Adds a NEW license. <see cref="LicenseRecord.ActivationKey"/> is the plaintext key; only its hash is stored.</summary>
    public async Task AddAsync(LicenseRecord record, CancellationToken cancellationToken = default)
    {
        record.RowVersion = 1;

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Licenses.Add(ToEntity(record, ActivationKeys.Hash(record.ActivationKey)));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Writes the mutable fields if (and only if) nobody changed the license since it was read.</summary>
    public async Task SaveAsync(LicenseRecord record, CancellationToken cancellationToken = default)
    {
        var modules = JsonSerializer.Serialize(record.Modules);
        var features = JsonSerializer.Serialize(record.Features);
        var expected = record.RowVersion;
        var next = expected + 1;

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var changed = await db.Licenses
            .Where(l => l.Id == record.LicenseId && l.RowVersion == expected)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.ValidUntil, record.ValidUntil)
                .SetProperty(l => l.ModulesJson, modules)
                .SetProperty(l => l.FeaturesJson, features)
                .SetProperty(l => l.InstallationId, record.InstallationId)
                .SetProperty(l => l.Status, (int)record.Status)
                .SetProperty(l => l.Version, record.Version)
                .SetProperty(l => l.ActivatedAt, record.ActivatedAt)
                .SetProperty(l => l.LastIssuedAt, record.LastIssuedAt)
                .SetProperty(l => l.RowVersion, next), cancellationToken);

        if (changed == 0)
            throw new LicenseConcurrencyException(record.LicenseId);

        record.RowVersion = next;
    }

    public async Task<LicensePage> ListAsync(LicenseFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = Filtered(db, filter);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(l => l.ValidFrom).ThenBy(l => l.Id)
            .Skip((Math.Max(page, 1) - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);

        return new LicensePage(items.Select(ToRecord).ToList(), total);
    }

    public async Task<int> CountAsync(LicenseFilter filter, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await Filtered(db, filter).CountAsync(cancellationToken);
    }

    private static IQueryable<LicenseEntity> Filtered(CloudDbContext db, LicenseFilter f)
    {
        var query = db.Licenses.AsNoTracking();
        if (f.CustomerId is not null) query = query.Where(l => l.CustomerId == f.CustomerId);
        if (f.Status is { } status) query = query.Where(l => l.Status == (int)status);
        if (f.Bound is { } bound) query = query.Where(l => (l.InstallationId != null) == bound);
        return query;
    }

    private static LicenseEntity ToEntity(LicenseRecord r, string keyHash) => new()
    {
        Id = r.LicenseId,
        CustomerId = r.CustomerId,
        ActivationKeyHash = keyHash,
        ProductId = r.ProductId,
        ValidFrom = r.ValidFrom,
        ValidUntil = r.ValidUntil,
        ModulesJson = JsonSerializer.Serialize(r.Modules),
        FeaturesJson = JsonSerializer.Serialize(r.Features),
        InstallationId = r.InstallationId,
        Status = (int)r.Status,
        Version = r.Version,
        ActivatedAt = r.ActivatedAt,
        LastIssuedAt = r.LastIssuedAt,
        RowVersion = r.RowVersion
    };

    private static LicenseRecord ToRecord(LicenseEntity e) => new()
    {
        LicenseId = e.Id,
        CustomerId = e.CustomerId,
        ActivationKey = e.ActivationKeyHash,
        ProductId = e.ProductId,
        ValidFrom = e.ValidFrom,
        ValidUntil = e.ValidUntil,
        Modules = JsonSerializer.Deserialize<List<string>>(e.ModulesJson) ?? [],
        Features = JsonSerializer.Deserialize<List<string>>(e.FeaturesJson) ?? [],
        InstallationId = e.InstallationId,
        Status = (LicenseStatusClaim)e.Status,
        Version = e.Version,
        ActivatedAt = e.ActivatedAt,
        LastIssuedAt = e.LastIssuedAt,
        RowVersion = e.RowVersion
    };
}
