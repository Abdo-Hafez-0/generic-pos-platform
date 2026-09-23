using Platform.Core.Results;

namespace Platform.Application.Abstractions.Messaging;

/// <summary>
/// Marker interface for a command — an operation that changes state.
/// Commands return a Result to signal success or failure.
/// Business logic must not be placed in the command itself.
/// Commands belong to the Application layer.
/// </summary>
public interface ICommand : IRequest<Result>;

/// <summary>
/// Marker interface for a command that returns a typed result.
/// </summary>
/// <typeparam name="TResponse">The type of value returned on success.</typeparam>
public interface ICommand<TResponse> : IRequest<Result<TResponse>>;
