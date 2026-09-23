using Inventory.Contracts.Models;
using Inventory.Domain.ValueObjects;
using Inventory.Application.Repositories;
using Platform.Core.Results;

namespace Inventory.Application.Queries;

// ============================================================
// GetStockLevelQuery
// ============================================================

public sealed record GetStockLevelQuery(Guid StockItemId);

public sealed class GetStockLevelQueryHandler(
    IStockItemRepository stockItemRepository,
    IInventoryBalanceRepository balanceRepository)
{
    public async Task<Result<StockLevelDto?>> HandleAsync(
        GetStockLevelQuery query,
        CancellationToken cancellationToken = default)
    {
        var stockItemId = new StockItemId(query.StockItemId);
        var stockItem = await stockItemRepository.GetByIdAsync(stockItemId, cancellationToken);
        if (stockItem is null)
            return Result.Success<StockLevelDto?>(null);

        var balance = await balanceRepository.GetByStockItemAsync(stockItemId, cancellationToken);
        var onHand = balance?.OnHand.Value ?? 0m;

        var dto = new StockLevelDto(
            stockItem.Id.Value,
            stockItem.CatalogProductId,
            stockItem.WarehouseId.Value,
            stockItem.LocationId?.Value,
            onHand);

        return Result.Success<StockLevelDto?>(dto);
    }
}

// ============================================================
// GetAllStockLevelsQuery
// ============================================================

public sealed record GetAllStockLevelsQuery();

public sealed class GetAllStockLevelsQueryHandler(
    IStockItemRepository stockItemRepository,
    IInventoryBalanceRepository balanceRepository)
{
    public async Task<Result<IReadOnlyList<StockLevelDto>>> HandleAsync(
        GetAllStockLevelsQuery query,
        CancellationToken cancellationToken = default)
    {
        var stockItems = await stockItemRepository.GetAllAsync(cancellationToken);
        var balances = await balanceRepository.GetAllAsync(cancellationToken);

        var balanceLookup = balances.ToDictionary(b => b.StockItemId);

        var dtos = stockItems.Select(item =>
        {
            var onHand = balanceLookup.TryGetValue(item.Id, out var bal) ? bal.OnHand.Value : 0m;
            return new StockLevelDto(
                item.Id.Value,
                item.CatalogProductId,
                item.WarehouseId.Value,
                item.LocationId?.Value,
                onHand);
        }).ToList();

        return Result.Success<IReadOnlyList<StockLevelDto>>(dtos);
    }
}
