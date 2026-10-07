using Inventory.Contracts.Interfaces;
using Inventory.Contracts.Models;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Application.Queries;
using POS.Application.Repositories;
using POS.Domain.Entities;

namespace POS.Tests.Application;

/// <summary>FIX-01b: the two reads the cashier screen needs - the cashier's open till (to resume it) and the warehouses to sell from.</summary>
public sealed class PosScreenQueriesTests
{
    private sealed class Warehouses(params WarehouseDto[] warehouses) : IInventoryReader
    {
        public Task<IReadOnlyList<WarehouseDto>> GetAllWarehousesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WarehouseDto>>(warehouses);

        public Task<StockLevelDto?> GetStockLevelAsync(Guid stockItemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StockLevelDto?> GetStockLevelByProductAsync(Guid catalogProductId, Guid warehouseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<StockLevelDto>> GetAllStockLevelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static async Task<PosSession> SaveAsync(PosTestDatabase db, string cashier, bool close = false)
    {
        var session = PosSession.Open(cashier, Guid.NewGuid()).Value;
        if (close) Assert.True(session.Close().IsSuccess);

        using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPosSessionRepository>().AddAsync(session);
        await scope.ServiceProvider.GetRequiredService<IPosUnitOfWork>().SaveChangesAsync();
        return session;
    }

    private static async Task<Guid?> FindAsync(PosTestDatabase db, string cashier)
    {
        using var scope = db.CreateScope();
        var handler = new GetOpenSessionForCashierQueryHandler(scope.ServiceProvider.GetRequiredService<IPosSessionRepository>());
        return (await handler.HandleAsync(new GetOpenSessionForCashierQuery(cashier)))?.SessionId;
    }

    [Fact]
    public async Task The_open_session_of_the_cashier_is_found_and_closed_ones_or_other_cashiers_are_not()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        await SaveAsync(db, "alice", close: true);
        var open = await SaveAsync(db, "alice");
        await SaveAsync(db, "bob");

        Assert.Equal(open.Id.Value, await FindAsync(db, "alice"));
        Assert.Equal(open.Id.Value, await FindAsync(db, " alice "));
        Assert.Null(await FindAsync(db, "carol"));
        Assert.Null(await FindAsync(db, " "));
    }

    [Fact]
    public async Task A_cashier_whose_sessions_are_all_closed_has_no_open_session()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        await SaveAsync(db, "alice", close: true);

        Assert.Null(await FindAsync(db, "alice"));
    }

    [Fact]
    public async Task Only_active_warehouses_are_offered_ordered_by_name()
    {
        var kiosk = Guid.NewGuid();
        var main = Guid.NewGuid();
        var handler = new GetSaleWarehousesQueryHandler(new Warehouses(
            new WarehouseDto(main, "Main shop", "MAIN", true),
            new WarehouseDto(Guid.NewGuid(), "Old store", "OLD", false),
            new WarehouseDto(kiosk, "Kiosk", "KIOSK", true)));

        var offered = await handler.HandleAsync(new GetSaleWarehousesQuery());

        Assert.Equal([kiosk, main], offered.Select(w => w.WarehouseId));
        Assert.Equal(("MAIN", "Main shop"), (offered[1].Code, offered[1].Name));
    }
}
