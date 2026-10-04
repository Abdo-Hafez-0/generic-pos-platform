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

        return new SaleDto(
            SaleId: sale.Id.Value,
            Status: sale.Status,
            Reference: sale.Reference,
            Notes: sale.Notes,
            SubTotal: sale.SubTotal.Amount,
            TaxTotal: sale.TaxTotal.Amount,
            GrandTotal: sale.GrandTotal.Amount,
            CreatedAt: sale.CreatedAt,
            UpdatedAt: sale.UpdatedAt,
            CompletedAt: sale.CompletedAt,
            CancelledAt: sale.CancelledAt,
            CancellationReason: sale.CancellationReason,
            Items: sale.Items.Select(i => new SaleItemDto(
                SaleItemId: i.Id.Value,
                CatalogProductId: i.CatalogProductId,
                ProductName: i.ProductName,
                ProductSku: i.ProductSku,
                Quantity: i.Quantity.Value,
                UnitPrice: i.UnitPrice.Amount,
                Discount: i.Discount.Amount,
                TaxRate: i.TaxRate,
                SubTotal: i.SubTotal.Amount,
                TaxAmount: i.TaxAmount.Amount,
                LineTotal: i.LineTotal.Amount
            )).ToList().AsReadOnly());
    }
}
