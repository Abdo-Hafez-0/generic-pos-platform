using Microsoft.EntityFrameworkCore;
using Purchasing.Application.Repositories;
using Purchasing.Domain.Entities;
using Purchasing.Domain.ValueObjects;
using Purchasing.Infrastructure.Persistence;

namespace Purchasing.Infrastructure.Repositories;

internal sealed class EfSupplierReturnRepository(PurchasingDbContext dbContext) : ISupplierReturnRepository
{
    public async Task AddAsync(SupplierReturn supplierReturn, CancellationToken cancellationToken = default)
        => await dbContext.SupplierReturns.AddAsync(supplierReturn, cancellationToken);

    public async Task<IReadOnlyList<SupplierReturn>> ListByOrderAsync(PurchaseOrderId orderId, CancellationToken cancellationToken = default)
        => await dbContext.SupplierReturns.AsNoTracking().Include(r => r.Lines)
            .Where(r => r.PurchaseOrderId == orderId)
            .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Number)
            .ToListAsync(cancellationToken);
}
