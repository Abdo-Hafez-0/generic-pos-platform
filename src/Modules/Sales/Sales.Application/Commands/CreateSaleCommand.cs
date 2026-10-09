using Catalog.Contracts.Interfaces;
using Inventory.Contracts.Interfaces;
using Platform.Core.Results;
using Sales.Application.Abstractions;
using Sales.Application.DTOs;
using Sales.Application.Repositories;
using Sales.Domain.Entities;
using Sales.Domain.ValueObjects;

namespace Sales.Application.Commands;

// ============================================================
// CreateSaleCommand
// ============================================================

/// <summary>
/// Creates a new Sale in Draft status.
///
/// FLOW:
///   1. Create the Sale aggregate in Draft status.
///   2. Persist and return the new SaleId.
///
/// Architecture: Sales.Application — no EF Core, no WPF.
/// Cross-module contracts consumed: IProductLookup, IStockAvailabilityChecker
///   (injected for use in AddItem operations, not needed for bare create).
/// </summary>
public sealed record CreateSaleCommand(
    string? Reference = null,
    string? Notes = null,
    Sales.Contracts.Models.SaleCustomer? Customer = null);

public sealed class CreateSaleCommandHandler(
    ISaleRepository saleRepository,
    ISalesUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        CreateSaleCommand command,
        CancellationToken cancellationToken = default)
    {
        var saleResult = Sale.Create(command.Reference, command.Notes);
        if (saleResult.IsFailure)
            return Result.Failure<Guid>(saleResult.Error);

        // FIX-11: the customer the caller (POS) found through Customers.Contracts, recorded as a snapshot
        if (command.Customer is { } customer)
        {
            var assigned = saleResult.Value.AssignCustomer(customer.CustomerId, customer.Code, customer.Name);
            if (assigned.IsFailure) return Result.Failure<Guid>(assigned.Error);
        }

        await saleRepository.AddAsync(saleResult.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(saleResult.Value.Id.Value);
    }
}
