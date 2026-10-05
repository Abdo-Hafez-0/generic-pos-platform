using Suppliers.Application.Repositories;
using Suppliers.Domain.Entities;
using Suppliers.Domain.Enums;
using Suppliers.Domain.ValueObjects;
using Suppliers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Suppliers.Infrastructure.Repositories;

internal sealed class EfSupplierRepository(SuppliersDbContext dbContext) : ISupplierRepository
{
    public async Task<Supplier?> GetByIdAsync(SupplierId id, CancellationToken cancellationToken = default)
        => await dbContext.Suppliers
            .Include(c => c.Addresses)
            .Include(c => c.Contacts)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<Supplier?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return await dbContext.Suppliers.FirstOrDefaultAsync(c => c.Code == normalized, cancellationToken);
    }

    public async Task AddAsync(Supplier supplier, CancellationToken cancellationToken = default)
        => await dbContext.Suppliers.AddAsync(supplier, cancellationToken);

    public async Task<IReadOnlyList<Supplier>> ListAsync(int skip, int take, SupplierStatus? status, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Suppliers.AsNoTracking();
        if (status is { } s) query = query.Where(c => c.Status == s);

        return await query.OrderBy(c => c.Name).ThenBy(c => c.Code).Skip(skip).Take(take).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Supplier>> SearchAsync(string text, int take, CancellationToken cancellationToken = default)
    {
        var pattern = $"%{Escape(text)}%";
        return await dbContext.Suppliers.AsNoTracking()
            .Where(c => EF.Functions.Like(c.Code, pattern, "\\")
                     || EF.Functions.Like(c.Name, pattern, "\\")
                     || (c.Email != null && EF.Functions.Like(c.Email, pattern, "\\"))
                     || (c.Phone != null && EF.Functions.Like(c.Phone, pattern, "\\")))
            .OrderBy(c => c.Name)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountAsync(SupplierStatus? status, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Suppliers.AsQueryable();
        if (status is { } s) query = query.Where(c => c.Status == s);
        return await query.CountAsync(cancellationToken);
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
