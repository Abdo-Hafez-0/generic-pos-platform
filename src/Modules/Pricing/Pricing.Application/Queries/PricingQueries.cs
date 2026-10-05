using Pricing.Application.DTOs;
using Pricing.Application.Repositories;
using Pricing.Domain.Entities;
using Pricing.Domain.Services;
using Pricing.Domain.ValueObjects;

namespace Pricing.Application.Queries;

internal static class PricingMapping
{
    public static PriceDto ToDto(this Price p)
        => new(p.Id.Value, p.PriceListId.Value, p.ProductId, p.Amount.Amount, p.MinimumQuantity, p.EffectiveFrom, p.EffectiveTo, p.Status);

    public static PriceListDto ToDto(this PriceList l) => new(l.Id.Value, l.Code, l.Name, l.IsDefault, l.Status);
}

public sealed record GetPriceQuery(Guid PriceId);

public sealed class GetPriceQueryHandler(IPriceRepository prices)
{
    public async Task<PriceDto?> HandleAsync(GetPriceQuery query, CancellationToken cancellationToken = default)
        => (await prices.GetByIdAsync(new PriceId(query.PriceId), cancellationToken))?.ToDto();
}

public sealed record ListPricesForProductQuery(Guid ProductId, Guid? PriceListId = null);

public sealed class ListPricesForProductQueryHandler(IPriceRepository prices)
{
    public async Task<IReadOnlyList<PriceDto>> HandleAsync(ListPricesForProductQuery query, CancellationToken cancellationToken = default)
        => (await prices.ListForProductAsync(query.ProductId, query.PriceListId is { } id ? new PriceListId(id) : null, cancellationToken))
            .OrderBy(p => p.MinimumQuantity).ThenByDescending(p => p.EffectiveFrom)
            .Select(p => p.ToDto()).ToList();
}

public sealed record ListPriceListsQuery;

public sealed class ListPriceListsQueryHandler(IPriceListRepository lists)
{
    public async Task<IReadOnlyList<PriceListDto>> HandleAsync(ListPriceListsQuery query, CancellationToken cancellationToken = default)
        => (await lists.ListAsync(cancellationToken)).Select(l => l.ToDto()).ToList();
}

/// <summary>"What is the current price of this product?" - the use case behind the IPriceResolver contract.</summary>
public sealed record GetCurrentPriceQuery(Guid ProductId, decimal Quantity = 1m, DateTime? At = null, Guid? PriceListId = null);

public sealed record CurrentPriceDto(Guid PriceId, Guid PriceListId, string PriceListCode, decimal Amount);

public sealed class GetCurrentPriceQueryHandler(IPriceRepository prices, IPriceListRepository lists)
{
    public async Task<CurrentPriceDto?> HandleAsync(GetCurrentPriceQuery query, CancellationToken cancellationToken = default)
    {
        if (query.ProductId == Guid.Empty || query.Quantity <= 0m) return null;

        var list = query.PriceListId is { } id
            ? await lists.GetByIdAsync(new PriceListId(id), cancellationToken)
            : await lists.GetDefaultAsync(cancellationToken);
        if (list is null || list.Status != Domain.Enums.PriceListStatus.Active) return null;

        var candidates = await prices.ListActiveForProductAsync(query.ProductId, list.Id, cancellationToken);
        var selected = PriceSelection.Select(candidates, query.At ?? DateTime.UtcNow, query.Quantity);
        return selected is null ? null : new CurrentPriceDto(selected.Id.Value, list.Id.Value, list.Code, selected.Amount.Amount);
    }
}
