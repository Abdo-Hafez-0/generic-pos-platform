using Catalog.Contracts.Interfaces;
using Inventory.Application.Repositories;
using Platform.Core.Results;

namespace Inventory.Application.Queries;

// ============================================================
// GetStockOverviewQuery (FIX-01c: the stock screen)
// ============================================================

/// <summary>One stock item as the stock screen shows it: the product's SKU and name (read through Catalog.Contracts) and the warehouse name.</summary>
public sealed record StockOverviewItem(
    Guid StockItemId,
    Guid ProductId,
    string Sku,
    string ProductName,
    Guid WarehouseId,
    string WarehouseName,
    decimal OnHand);

/// <summary>
/// Stock on hand per product and warehouse, with names a person can read. Optional filters: a warehouse, and text matched against the
/// product's SKU or name (case-insensitive). Ordered by product name, then warehouse. A product Catalog no longer knows is shown by its ID,
/// never hidden: stock must stay visible whatever happened to the product.
/// </summary>
public sealed record GetStockOverviewQuery(string? Search = null, Guid? WarehouseId = null);

public sealed class GetStockOverviewQueryHandler(
    IStockItemRepository stockItemRepository,
    IInventoryBalanceRepository balanceRepository,
    IWarehouseRepository warehouseRepository,
    IProductLookup productLookup)
{
    public async Task<Result<IReadOnlyList<StockOverviewItem>>> HandleAsync(
        GetStockOverviewQuery query,
        CancellationToken cancellationToken = default)
    {
        var stockItems = await stockItemRepository.GetAllAsync(cancellationToken);
        var balances = (await balanceRepository.GetAllAsync(cancellationToken)).ToDictionary(b => b.StockItemId, b => b.OnHand.Value);
        var warehouses = (await warehouseRepository.GetAllAsync(cancellationToken)).ToDictionary(w => w.Id.Value, w => w.Name);

        var products = new Dictionary<Guid, (string Sku, string Name)>();
        var items = new List<StockOverviewItem>();
        foreach (var item in stockItems)
        {
            if (query.WarehouseId is { } warehouseId && item.WarehouseId.Value != warehouseId)
                continue;

            if (!products.TryGetValue(item.CatalogProductId, out var product))
            {
                var found = await productLookup.FindByIdAsync(item.CatalogProductId, cancellationToken);
                products[item.CatalogProductId] = product = found is null
                    ? (item.CatalogProductId.ToString("D"), item.CatalogProductId.ToString("D"))
                    : (found.Sku, found.Name);
            }

            if (!string.IsNullOrWhiteSpace(query.Search)
                && !product.Sku.Contains(query.Search.Trim(), StringComparison.CurrentCultureIgnoreCase)
                && !product.Name.Contains(query.Search.Trim(), StringComparison.CurrentCultureIgnoreCase))
                continue;

            items.Add(new StockOverviewItem(
                item.Id.Value,
                item.CatalogProductId,
                product.Sku,
                product.Name,
                item.WarehouseId.Value,
                warehouses.GetValueOrDefault(item.WarehouseId.Value, item.WarehouseId.Value.ToString("D")),
                balances.GetValueOrDefault(item.Id, 0m)));
        }

        return Result.Success<IReadOnlyList<StockOverviewItem>>(items
            .OrderBy(i => i.ProductName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(i => i.WarehouseName, StringComparer.CurrentCultureIgnoreCase)
            .ToList());
    }
}

// ============================================================
// FindStockProductQuery (FIX-01c: receiving stock by SKU or barcode)
// ============================================================

/// <summary>A product stock can be received for, as the stock screen shows it.</summary>
public sealed record StockProduct(Guid ProductId, string Sku, string Name);

/// <summary>
/// Finds the product a typed or scanned code means: its SKU first, then (when Catalog's barcode resolver is composed) a barcode.
/// Inactive products are found too: stock of a product that is no longer sold can still be counted and corrected.
/// </summary>
public sealed record FindStockProductQuery(string Code);

public sealed class FindStockProductQueryHandler(IProductLookup productLookup, IProductBarcodeResolver? barcodes = null)
{
    public async Task<Result<StockProduct>> HandleAsync(FindStockProductQuery query, CancellationToken cancellationToken = default)
    {
        var code = query.Code?.Trim() ?? string.Empty;
        if (code.Length == 0)
            return Result.Failure<StockProduct>(Error.Validation("Inventory.Product.CodeRequired", "Enter a SKU or scan a barcode."));

        var product = await productLookup.FindBySkuAsync(code, cancellationToken)
            ?? (barcodes is null ? null : await barcodes.ResolveAsync(code, cancellationToken));

        return product is null
            ? Result.Failure<StockProduct>(Error.NotFound("Inventory.Product.NotFound", $"No product has the SKU or barcode '{code}'."))
            : Result.Success(new StockProduct(product.ProductId, product.Sku, product.Name));
    }
}
