using Suppliers.Domain.ValueObjects;

namespace Suppliers.Domain.Events;

public sealed record SupplierCreatedEvent(SupplierId SupplierId, string Code, DateTime OccurredAt);

public sealed record SupplierDeactivatedEvent(SupplierId SupplierId, DateTime OccurredAt);
