using Sales.Domain.ValueObjects;

namespace Sales.Domain.Events;

/// <summary>
/// Raised when a new Sale is created in Draft status.
/// Architecture: Sales.Domain — domain event, no infrastructure dependency.
/// </summary>
public sealed record SaleCreatedEvent(SaleId SaleId, DateTime OccurredAt);
