using Platform.Core.Domain;

namespace Platform.Contracts.Events;

/// <summary>
/// Contract for handling a specific domain event type.
/// Modules implement this interface to subscribe to events from other modules
/// without creating direct dependencies on those modules' implementations.
/// </summary>
/// <typeparam name="TEvent">The specific domain event type to handle.</typeparam>
public interface IDomainEventHandler<in TEvent> where TEvent : DomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken = default);
}
