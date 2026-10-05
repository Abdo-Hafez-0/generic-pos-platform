using Customers.Domain.ValueObjects;

namespace Customers.Domain.Events;

public sealed record CustomerCreatedEvent(CustomerId CustomerId, string Code, DateTime OccurredAt);

public sealed record CustomerDeactivatedEvent(CustomerId CustomerId, DateTime OccurredAt);
