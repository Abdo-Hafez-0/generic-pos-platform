using POS.Application.Mapping;
using POS.Application.Repositories;
using POS.Contracts.Models;
using POS.Domain.ValueObjects;

namespace POS.Application.Queries;

public sealed record GetPosSessionQuery(Guid SessionId);

public sealed class GetPosSessionQueryHandler(IPosSessionRepository sessionRepository)
{
    public async Task<POSSessionResult?> HandleAsync(
        GetPosSessionQuery query,
        CancellationToken cancellationToken = default)
    {
        var session = await sessionRepository.GetByIdAsync(new PosSessionId(query.SessionId), cancellationToken);
        return session?.ToResult();
    }
}

public sealed record GetCartQuery(Guid CartId);

public sealed class GetCartQueryHandler(IPosCartRepository cartRepository)
{
    public async Task<POSCartResult?> HandleAsync(
        GetCartQuery query,
        CancellationToken cancellationToken = default)
    {
        var cart = await cartRepository.GetByIdAsync(new PosCartId(query.CartId), cancellationToken);
        return cart?.ToResult();
    }
}

/// <summary>Returns the session's open cart, or null when none has been started.</summary>
public sealed record GetCurrentCartQuery(Guid SessionId);

public sealed class GetCurrentCartQueryHandler(IPosCartRepository cartRepository)
{
    public async Task<POSCartResult?> HandleAsync(
        GetCurrentCartQuery query,
        CancellationToken cancellationToken = default)
    {
        var cart = await cartRepository.GetOpenCartForSessionAsync(new PosSessionId(query.SessionId), cancellationToken);
        return cart?.ToResult();
    }
}
