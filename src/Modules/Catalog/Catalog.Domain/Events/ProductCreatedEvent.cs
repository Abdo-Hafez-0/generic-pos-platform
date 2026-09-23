using Catalog.Domain.ValueObjects;

namespace Catalog.Domain.Events;

/// <summary>
/// Domain event raised when a new product is created in the catalog.
/// Published after the Product aggregate is persisted.
/// </summary>
public sealed record ProductCreatedEvent(
    ProductId ProductId,
    string Sku,
    string Name);
