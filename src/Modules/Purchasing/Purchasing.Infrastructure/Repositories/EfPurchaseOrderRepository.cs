using Microsoft.EntityFrameworkCore;
using Purchasing.Application.Repositories;
using Purchasing.Domain.Entities;
using Purchasing.Domain.Enums;
using Purchasing.Domain.ValueObjects;
using Purchasing.Infrastructure.Persistence;

namespace Purchasing.Infrastructure.Repositories;

internal sealed class EfPurchaseOrderRepository(PurchasingDbContext dbContext) : IPurchaseOrderRepository
{
    public async Task<PurchaseOrder?> GetByIdAsync(PurchaseOrderId id, CancellationToken cancellationToken = default)
        => await dbContext.PurchaseOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task AddAsync(PurchaseOrder order, CancellationToken cancellationToken = default)
        => await dbContext.PurchaseOrders.AddAsync(order, cancellationToken);

    public async Task<IReadOnlyList<PurchaseOrder>> ListAsync(int skip, int take, PurchaseOrderStatus? status, CancellationToken cancellationToken = default)
    {
        var query = dbContext.PurchaseOrders.AsNoTracking();
        if (status is { } s) query = query.Where(o => o.Status == s);

        return await query.OrderByDescending(o => o.CreatedAt).ThenBy(o => o.Number).Skip(skip).Take(take).ToListAsync(cancellationToken);
    }

    public async Task<int> CountAsync(PurchaseOrderStatus? status, CancellationToken cancellationToken = default)
    {
        var query = dbContext.PurchaseOrders.AsQueryable();
        if (status is { } s) query = query.Where(o => o.Status == s);
        return await query.CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<(PurchaseOrderStatus Status, decimal Total, decimal Received)>> GetStatusTotalsAsync(CancellationToken cancellationToken = default)
    {
        // SQLite cannot aggregate decimals in SQL, so only (status, total, received) rows are loaded - never the lines.
        var rows = await dbContext.PurchaseOrders.AsNoTracking()
            .Select(o => new { o.Status, o.TotalAmount, o.ReceivedAmount })
            .ToListAsync(cancellationToken);

        return rows.Select(r => (r.Status, r.TotalAmount.Amount, r.ReceivedAmount.Amount)).ToList();
    }
}
