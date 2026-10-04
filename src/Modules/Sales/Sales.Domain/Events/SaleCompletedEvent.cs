using Sales.Domain.ValueObjects;

namespace Sales.Domain.Events;

/// <summary>
/// Raised when a Sale transitions to Completed status.
/// This is the event that signals downstream modules (e.g., Inventory) to act.
/// Architecture: Sales.Domain — domain event, no infrastructure dependency.
/// </summary>
public sealed record SaleCompletedEvent(SaleId SaleId, decimal TotalAmount, DateTime CompletedAt);
