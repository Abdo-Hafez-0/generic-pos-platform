using Catalog.Domain.ValueObjects;
using Catalog.Domain.Enums;

namespace Catalog.Domain.Events;

/// <summary>
/// Domain event raised when a product's status changes (Active/Inactive/Discontinued).
/// </summary>
public sealed record ProductStatusChangedEvent(
    ProductId ProductId,
    ProductStatus PreviousStatus,
    ProductStatus NewStatus);
