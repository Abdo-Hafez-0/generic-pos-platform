using Platform.Application.Abstractions.Authorization;
using Inventory.Domain.Entities;
using Inventory.Application.Abstractions;
using Inventory.Application.Repositories;
using Platform.Core.Results;

namespace Inventory.Application.Commands;

// ============================================================
// CreateWarehouseCommand
// ============================================================

public sealed record CreateWarehouseCommand(
    string Name,
    string Code,
    string? Description = null);

public sealed class CreateWarehouseCommandHandler(
    IWarehouseRepository warehouseRepository,
    IInventoryUnitOfWork unitOfWork,
    IAuthorizationService authorization)
{
    public async Task<Result<Guid>> HandleAsync(
        CreateWarehouseCommand command,
        CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Inventory.Application.Security.InventoryCapabilities.ManageLocations, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<Guid>(allowed.Error);

        // Guard: duplicate code
        if (await warehouseRepository.ExistsByCodeAsync(command.Code, cancellationToken))
            return Result.Failure<Guid>(Error.Conflict(
                "Inventory.Warehouse.DuplicateCode",
                $"A warehouse with code '{command.Code.Trim().ToUpperInvariant()}' already exists."));

        var createResult = Warehouse.Create(command.Name, command.Code, command.Description);
        if (createResult.IsFailure)
            return Result.Failure<Guid>(createResult.Error);

        var warehouse = createResult.Value;
        await warehouseRepository.AddAsync(warehouse, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(warehouse.Id.Value);
    }
}
