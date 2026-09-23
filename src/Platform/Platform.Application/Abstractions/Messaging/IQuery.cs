namespace Platform.Application.Abstractions.Messaging;

/// <summary>
/// Marker interface for a query — an operation that reads state without changing it.
/// Queries return data directly. They should never modify state.
/// </summary>
/// <typeparam name="TResponse">The type of data returned by the query.</typeparam>
public interface IQuery<TResponse> : IRequest<TResponse>;
