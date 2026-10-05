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
