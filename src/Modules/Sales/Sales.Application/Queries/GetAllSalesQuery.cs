using Sales.Application.DTOs;
using Sales.Application.Repositories;

namespace Sales.Application.Queries;

// ============================================================
// GetAllSalesQuery
// ============================================================

/// <summary>
/// Returns all Sales as DTOs (for listing/overview purposes).
/// </summary>
public sealed record GetAllSalesQuery;

public sealed class GetAllSalesQueryHandler(ISaleRepository saleRepository)
{
    public async Task<IReadOnlyList<SaleDto>> HandleAsync(
        GetAllSalesQuery query,
        CancellationToken cancellationToken = default)
    {
        var sales = await saleRepository.GetAllAsync(cancellationToken);

        return sales.Select(sale => new SaleDto(
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
            )).ToList().AsReadOnly()
        )).ToList().AsReadOnly();
    }
}
