using Platform.Core.Results;
using Sales.Application.DTOs;
using Sales.Application.Repositories;
using Sales.Domain.ValueObjects;

namespace Sales.Application.Queries;

// ============================================================
// GetSaleByIdQuery
// ============================================================

/// <summary>
/// Returns a Sale and its items by ID.
/// Returns null if not found.
/// </summary>
public sealed record GetSaleByIdQuery(Guid SaleId);

public sealed class GetSaleByIdQueryHandler(ISaleRepository saleRepository)
{
    public async Task<SaleDto?> HandleAsync(
        GetSaleByIdQuery query,
        CancellationToken cancellationToken = default)
    {
        var sale = await saleRepository.GetByIdAsync(new SaleId(query.SaleId), cancellationToken);
        if (sale is null)
            return null;

        return SaleDtoMapper.ToDto(sale);
    }
}
