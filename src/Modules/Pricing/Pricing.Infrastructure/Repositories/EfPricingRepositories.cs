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

internal sealed class EfTaxRateRepository(PricingDbContext dbContext) : ITaxRateRepository
{
    public async Task<TaxRate?> GetByIdAsync(TaxRateId id, CancellationToken cancellationToken = default)
        => await dbContext.TaxRates.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<TaxRate?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return await dbContext.TaxRates.FirstOrDefaultAsync(r => r.Code == normalized, cancellationToken);
    }

    public async Task<TaxRate?> GetDefaultAsync(CancellationToken cancellationToken = default)
        => await dbContext.TaxRates.FirstOrDefaultAsync(r => r.IsDefault && r.Status == TaxRateStatus.Active, cancellationToken);

    public async Task<IReadOnlyList<TaxRate>> ListAsync(CancellationToken cancellationToken = default)
        => await dbContext.TaxRates.OrderBy(r => r.Code).ToListAsync(cancellationToken);

    public async Task AddAsync(TaxRate rate, CancellationToken cancellationToken = default)
        => await dbContext.TaxRates.AddAsync(rate, cancellationToken);
}

internal sealed class EfProductTaxRateRepository(PricingDbContext dbContext) : IProductTaxRateRepository
{
    public async Task<ProductTaxRate?> GetAsync(Guid productId, CancellationToken cancellationToken = default)
        => await dbContext.ProductTaxRates.FirstOrDefaultAsync(a => a.ProductId == productId, cancellationToken);

    public async Task AddAsync(ProductTaxRate assignment, CancellationToken cancellationToken = default)
        => await dbContext.ProductTaxRates.AddAsync(assignment, cancellationToken);

    public void Remove(ProductTaxRate assignment) => dbContext.ProductTaxRates.Remove(assignment);
}
