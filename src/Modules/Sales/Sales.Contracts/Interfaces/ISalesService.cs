using Sales.Contracts.Models;

namespace Sales.Contracts.Interfaces;

/// <summary>
/// Allows other modules to create a new Sale and add items, through a stable contract.
/// 
/// This is the contract that POS will use to drive a sale without depending on Sales.Application
/// or Sales.Infrastructure directly.
///
/// Implemented by Sales.Infrastructure.Services.SalesService.
/// Consumed by: POS (Stage 5D).
///
/// Cross-module contract: uses only DTOs/results, never Sales.Domain entities.
/// Architecture reference: Module Map §24 (POS Dependency on Sales.Contracts).
/// </summary>
public interface ISalesService
{
    /// <summary>Creates a new Sale. Returns the new SaleId on success.</summary>
    Task<CreateSaleResult> CreateSaleAsync(
        string? reference = null,
        string? notes = null,
        CancellationToken cancellationToken = default);

    /// <summary>Adds an item to a Draft Sale. Returns the new SaleItemId on success.</summary>
    Task<AddSaleItemResult> AddItemAsync(
        Guid saleId,
        Guid catalogProductId,
        decimal quantity,
        decimal unitPrice,
        decimal discount = 0m,
        decimal taxRate = 0m,
        Guid? warehouseId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Confirms a Draft Sale (no more item changes allowed).</summary>
    Task<SaleOperationResult> ConfirmSaleAsync(Guid saleId, CancellationToken cancellationToken = default);

    /// <summary>Completes a Confirmed Sale and records the SalesTransaction.</summary>
    Task<SaleOperationResult> CompleteSaleAsync(Guid saleId, string? transactionReference = null, CancellationToken cancellationToken = default);

    /// <summary>Cancels a Draft or Confirmed Sale.</summary>
    Task<SaleOperationResult> CancelSaleAsync(Guid saleId, string reason, CancellationToken cancellationToken = default);
}
