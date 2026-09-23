using Platform.Core.Domain;

namespace Platform.Contracts.Events;

/// <summary>
/// Contract for publishing domain events to subscribers.
/// Implementations are registered by Platform.Infrastructure.
/// Modules publish events through this interface without knowing who the subscribers are.
/// This is the primary mechanism for decoupled cross-module side effects.
/// </summary>
public interface IDomainEventPublisher
{
    /// <summary>
    /// Publishes a domain event to all registered handlers.
    /// </summary>
    Task PublishAsync(DomainEvent domainEvent, CancellationToken cancellationToken = default);
}
