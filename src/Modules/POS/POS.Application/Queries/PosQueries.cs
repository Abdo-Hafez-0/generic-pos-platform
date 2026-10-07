using Inventory.Contracts.Interfaces;
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

/// <summary>
/// The till session the cashier left open (for example before signing out or closing the application), so the cashier screen can resume it
/// instead of opening a second one. Null when the cashier has no open session.
/// </summary>
public sealed record GetOpenSessionForCashierQuery(string CashierReference);

public sealed class GetOpenSessionForCashierQueryHandler(IPosSessionRepository sessionRepository)
{
    public async Task<POSSessionResult?> HandleAsync(
        GetOpenSessionForCashierQuery query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.CashierReference))
            return null;

        var session = await sessionRepository.GetOpenSessionForCashierAsync(query.CashierReference.Trim(), cancellationToken);
        return session?.ToResult();
    }
}

/// <summary>The active warehouses a till session can sell from (read through Inventory.Contracts), ordered by name.</summary>
public sealed record GetSaleWarehousesQuery;

public sealed class GetSaleWarehousesQueryHandler(IInventoryReader inventory)
{
    public async Task<IReadOnlyList<POSWarehouseResult>> HandleAsync(
        GetSaleWarehousesQuery query,
        CancellationToken cancellationToken = default)
    {
        var warehouses = await inventory.GetAllWarehousesAsync(cancellationToken);
        return warehouses
            .Where(w => w.IsActive)
            .OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(w => new POSWarehouseResult(w.WarehouseId, w.Code, w.Name))
            .ToList();
    }
}
