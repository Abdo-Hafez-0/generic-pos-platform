using Inventory.Contracts.Models;
using Inventory.Domain.ValueObjects;
using Inventory.Application.Repositories;
using Platform.Core.Results;

namespace Inventory.Application.Queries;

// ============================================================
// GetStockMovementsQuery
// ============================================================

public sealed record GetStockMovementsQuery(Guid StockItemId);

public sealed class GetStockMovementsQueryHandler(IStockMovementRepository movementRepository)
{
    public async Task<Result<IReadOnlyList<StockMovementDto>>> HandleAsync(
        GetStockMovementsQuery query,
        CancellationToken cancellationToken = default)
    {
        var stockItemId = new StockItemId(query.StockItemId);
        var movements = await movementRepository.GetByStockItemAsync(stockItemId, cancellationToken);

        var dtos = movements.Select(m => new StockMovementDto(
            m.Id.Value,
            m.StockItemId.Value,
            m.MovementType.ToString(),
            m.Quantity.Value,
            m.Reference,
            m.OccurredAt)).ToList();

        return Result.Success<IReadOnlyList<StockMovementDto>>(dtos);
    }
}
