using Pricing.Application.Queries;
using Pricing.Contracts.Interfaces;
using Pricing.Contracts.Models;

namespace Pricing.Infrastructure.Services;

/// <summary>Implements IPriceResolver from Pricing.Contracts by delegating to the GetCurrentPrice use case.</summary>
internal sealed class PriceResolver(GetCurrentPriceQueryHandler handler) : IPriceResolver
{
    public async Task<PriceResolutionResult> ResolveAsync(
        Guid productId, decimal quantity = 1m, DateTime? at = null, Guid? priceListId = null, CancellationToken cancellationToken = default)
    {
        var current = await handler.HandleAsync(new GetCurrentPriceQuery(productId, quantity, at, priceListId), cancellationToken);
        return current is null
            ? PriceResolutionResult.None
            : new PriceResolutionResult(true, current.Amount, current.PriceId, current.PriceListId, current.PriceListCode);
    }
}

/// <summary>Implements ITaxRateResolver from Pricing.Contracts (FIX-08) by delegating to the GetProductTax use case.</summary>
internal sealed class TaxRateResolver(GetProductTaxQueryHandler handler) : ITaxRateResolver
{
    public async Task<TaxResolutionResult> ResolveAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        var tax = await handler.HandleAsync(new GetProductTaxQuery(productId), cancellationToken);
        return tax.Effective is { } rate
            ? new TaxResolutionResult(true, rate.Rate, rate.TaxRateId, rate.Code, rate.Name)
            : TaxResolutionResult.None;
    }
}

/// <summary>MISS-11b: implements Pricing.Contracts' tax rate listing (every rate, by code).</summary>
internal sealed class TaxRateListing(Pricing.Application.Repositories.ITaxRateRepository rates) : ITaxRateListing
{
    public async Task<IReadOnlyList<TaxRateListItem>> ListAsync(CancellationToken cancellationToken = default)
        => (await rates.ListAsync(cancellationToken))
            .Select(r => new TaxRateListItem(r.Id.Value, r.Code, r.Name, r.Rate, r.IsDefault, r.Status == Pricing.Domain.Enums.TaxRateStatus.Active))
            .OrderBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
