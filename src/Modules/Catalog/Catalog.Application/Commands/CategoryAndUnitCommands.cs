using Catalog.Domain.Entities;
using Catalog.Domain.ValueObjects;
using Catalog.Application.Repositories;
using Catalog.Application.Abstractions;
using Platform.Core.Results;

namespace Catalog.Application.Commands;

// ============================================================
// CreateCategoryCommand
// ============================================================

public sealed record CreateCategoryCommand(string Name, string? Description = null);

public sealed class CreateCategoryCommandHandler(
    ICategoryRepository categoryRepository,
    ICatalogUnitOfWork unitOfWork)
{
    public async Task<Result<CategoryId>> HandleAsync(
        CreateCategoryCommand command,
        CancellationToken cancellationToken = default)
    {
        var createResult = Category.Create(command.Name, command.Description);
        if (createResult.IsFailure)
            return Result.Failure<CategoryId>(createResult.Error);

        var category = createResult.Value;
        await categoryRepository.AddAsync(category, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(category.Id);
    }
}

// ============================================================
// CreateUnitCommand
// ============================================================

public sealed record CreateUnitCommand(string Name, string Abbreviation);

public sealed class CreateUnitCommandHandler(
    IUnitRepository unitRepository,
    ICatalogUnitOfWork unitOfWork)
{
    public async Task<Result<UnitId>> HandleAsync(
        CreateUnitCommand command,
        CancellationToken cancellationToken = default)
    {
        var createResult = Unit.Create(command.Name, command.Abbreviation);
        if (createResult.IsFailure)
            return Result.Failure<UnitId>(createResult.Error);

        var unit = createResult.Value;
        await unitRepository.AddAsync(unit, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(unit.Id);
    }
}
