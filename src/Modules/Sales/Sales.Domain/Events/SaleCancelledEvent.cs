using Sales.Domain.ValueObjects;

namespace Sales.Domain.Events;

/// <summary>
/// Raised when a Sale is cancelled.
/// Architecture: Sales.Domain — domain event, no infrastructure dependency.
/// </summary>
public sealed record SaleCancelledEvent(SaleId SaleId, string Reason, DateTime CancelledAt);
