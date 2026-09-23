namespace Platform.Application.Abstractions.Messaging;

/// <summary>
/// Marker interface for a request/response pair.
/// Used as the basis for ICommand and IQuery.
/// The platform dispatcher (Stage 2/4) will route requests to their handlers.
/// </summary>
/// <typeparam name="TResponse">The type of the response.</typeparam>
public interface IRequest<TResponse>;

/// <summary>
/// Handles a specific request type and produces a response.
/// One handler per request type is the convention.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken = default);
}
