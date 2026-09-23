using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;
using Inventory.Application.Abstractions;
using Inventory.Application.Repositories;
using Platform.Core.Results;

namespace Inventory.Application.Commands;

// ============================================================
// CreateLocationCommand
// ============================================================

public sealed record CreateLocationCommand(
    Guid WarehouseId,
    string Name,
    string Code,
    string? Description = null);

public sealed class CreateLocationCommandHandler(
    IWarehouseRepository warehouseRepository,
    ILocationRepository locationRepository,
    IInventoryUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        CreateLocationCommand command,
        CancellationToken cancellationToken = default)
    {
        var warehouseId = new WarehouseId(command.WarehouseId);

        // Guard: warehouse must exist and be active
        var warehouse = await warehouseRepository.GetByIdAsync(warehouseId, cancellationToken);
        if (warehouse is null)
            return Result.Failure<Guid>(Error.NotFound(
                "Inventory.Location.WarehouseNotFound",
                $"Warehouse '{command.WarehouseId}' was not found."));

        if (!warehouse.IsActive)
            return Result.Failure<Guid>(Error.Conflict(
                "Inventory.Location.WarehouseInactive",
                "Cannot create a location in an inactive warehouse."));

        // Guard: duplicate code within the same warehouse
        if (await locationRepository.ExistsByCodeInWarehouseAsync(warehouseId, command.Code, cancellationToken))
            return Result.Failure<Guid>(Error.Conflict(
                "Inventory.Location.DuplicateCode",
                $"A location with code '{command.Code.Trim().ToUpperInvariant()}' already exists in this warehouse."));

        var createResult = Location.Create(warehouseId, command.Name, command.Code, command.Description);
        if (createResult.IsFailure)
            return Result.Failure<Guid>(createResult.Error);

        var location = createResult.Value;
        await locationRepository.AddAsync(location, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(location.Id.Value);
    }
}
