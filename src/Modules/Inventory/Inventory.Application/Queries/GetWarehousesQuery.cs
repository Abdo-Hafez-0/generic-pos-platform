using Inventory.Contracts.Models;
using Inventory.Application.Repositories;
using Platform.Core.Results;

namespace Inventory.Application.Queries;

// ============================================================
// GetWarehousesQuery
// ============================================================

public sealed record GetWarehousesQuery(bool ActiveOnly = true);

public sealed class GetWarehousesQueryHandler(IWarehouseRepository warehouseRepository)
{
    public async Task<Result<IReadOnlyList<WarehouseDto>>> HandleAsync(
        GetWarehousesQuery query,
        CancellationToken cancellationToken = default)
    {
        var warehouses = query.ActiveOnly
            ? await warehouseRepository.GetActiveAsync(cancellationToken)
            : await warehouseRepository.GetAllAsync(cancellationToken);

        var dtos = warehouses
            .Select(w => new WarehouseDto(w.Id.Value, w.Name, w.Code, w.IsActive))
            .ToList();

        return Result.Success<IReadOnlyList<WarehouseDto>>(dtos);
    }
}
