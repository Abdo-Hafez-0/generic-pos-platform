namespace Platform.Core.Domain;

/// <summary>
/// Represents a domain event raised by an aggregate.
/// Domain events are facts that describe something that happened in the business domain.
/// They communicate across module boundaries without coupling implementations.
/// </summary>
public abstract class DomainEvent
{
    /// <summary>
    /// Unique identifier for this event occurrence.
    /// </summary>
    public Guid EventId { get; } = Guid.NewGuid();

    /// <summary>
    /// UTC timestamp when the event was raised.
    /// </summary>
    public DateTime OccurredOnUtc { get; } = DateTime.UtcNow;
}
