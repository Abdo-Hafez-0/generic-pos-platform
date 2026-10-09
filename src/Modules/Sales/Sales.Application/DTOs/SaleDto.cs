using Sales.Domain.Enums;

namespace Sales.Application.DTOs;

/// <summary>
/// DTO representing a Sale as seen by the Application layer and UI.
/// Does not expose domain entities directly.
/// </summary>
public sealed record SaleDto(
    Guid SaleId,
    SaleStatus Status,
    string? Reference,
    string? Notes,
    decimal SubTotal,
    decimal TaxTotal,
    decimal GrandTotal,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt,
    DateTime? CancelledAt,
    string? CancellationReason,
    IReadOnlyList<SaleItemDto> Items,
    Guid? CustomerId = null,
    string? CustomerCode = null,
    string? CustomerName = null);

/// <summary>
/// DTO representing a single line item within a Sale.
/// Includes the historical snapshot values (price, discount, tax at time of sale).
/// </summary>
public sealed record SaleItemDto(
    Guid SaleItemId,
    Guid CatalogProductId,
    string ProductName,
    string ProductSku,
    decimal Quantity,
    decimal UnitPrice,
    decimal Discount,
    decimal TaxRate,
    decimal SubTotal,
    decimal TaxAmount,
    decimal LineTotal);
