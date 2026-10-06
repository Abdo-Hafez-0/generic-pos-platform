using Catalog.Application.DTOs;
using Platform.Application.Abstractions.Authorization;

namespace Catalog.Application.Queries;

/// <summary>
/// Read decision for purchase cost: a product read is never refused (cashiers need products), it simply carries no cost unless the
/// caller holds catalog.cost.view. The module contracts other modules use (IProductLookup, e.g. Purchasing's default unit cost) are
/// trusted calls and keep the cost.
/// </summary>
internal static class CostVisibility
{
    public static async Task<ProductDto> ApplyAsync(ProductDto dto, IAuthorizationService authorization, CancellationToken cancellationToken)
        => dto.CostPrice is null || await authorization.IsAllowedAsync(Catalog.Application.Security.CatalogCapabilities.ViewCost, cancellationToken)
            ? dto
            : dto with { CostPrice = null };
}
