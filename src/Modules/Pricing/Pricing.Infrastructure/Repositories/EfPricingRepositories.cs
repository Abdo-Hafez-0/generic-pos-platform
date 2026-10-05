using Microsoft.EntityFrameworkCore;
using Pricing.Application.Repositories;
using Pricing.Domain.Entities;
using Pricing.Domain.Enums;
using Pricing.Domain.ValueObjects;
using Pricing.Infrastructure.Persistence;

namespace Pricing.Infrastructure.Repositories;

internal sealed class EfPriceListRepository(PricingDbContext dbContext) : IPriceListRepository
{
    public async Task<PriceList?> GetByIdAsync(PriceListId id, CancellationToken cancellationToken = default)
        => await dbContext.PriceLists.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);

    public async Task<PriceList?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return await dbContext.PriceLists.FirstOrDefaultAsync(l => l.Code == normalized, cancellationToken);
    }

    public async Task<PriceList?> GetDefaultAsync(CancellationToken cancellationToken = default)
        => await dbContext.PriceLists.FirstOrDefaultAsync(l => l.IsDefault && l.Status == PriceListStatus.Active, cancellationToken);

    public async Task<IReadOnlyList<PriceList>> ListAsync(CancellationToken cancellationToken = default)
        => await dbContext.PriceLists.OrderBy(l => l.Code).ToListAsync(cancellationToken);

    public async Task AddAsync(PriceList list, CancellationToken cancellationToken = default)
        => await dbContext.PriceLists.AddAsync(list, cancellationToken);
}

internal sealed class EfPriceRepository(PricingDbContext dbContext) : IPriceRepository
{
    public async Task<Price?> GetByIdAsync(PriceId id, CancellationToken cancellationToken = default)
        => await dbContext.Prices.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task AddAsync(Price price, CancellationToken cancellationToken = default)
        => await dbContext.Prices.AddAsync(price, cancellationToken);

    public async Task<IReadOnlyList<Price>> ListForProductAsync(Guid productId, PriceListId? priceListId, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Prices.Where(p => p.ProductId == productId);
        if (priceListId is { } id) query = query.Where(p => p.PriceListId == id);
        return await query.ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Price>> ListActiveForProductAsync(Guid productId, PriceListId priceListId, CancellationToken cancellationToken = default)
        => await dbContext.Prices.AsNoTracking()
            .Where(p => p.ProductId == productId && p.PriceListId == priceListId && p.Status == PriceStatus.Active)
            .ToListAsync(cancellationToken);
}
