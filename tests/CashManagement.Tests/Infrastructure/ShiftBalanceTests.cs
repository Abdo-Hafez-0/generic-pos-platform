using CashManagement.Application.Commands;
using CashManagement.Application.Queries;
using CashManagement.Application.Repositories;
using CashManagement.Contracts.Interfaces;
using CashManagement.Domain.Enums;
using CashManagement.Domain.ValueObjects;
using CashManagement.Infrastructure.DependencyInjection;
using CashManagement.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Tests.Common;

namespace CashManagement.Tests.Infrastructure;

/// <summary>
/// MISS-06a: a busy shift is no longer read in full for every cash sale. The shift keeps the total of its movements; recording one more
/// reads the shift without its history; "recorded once per reference" is checked against the stored movements and guarded by a unique
/// index; an upgraded database gets the total of the shifts it already holds.
/// </summary>
public sealed class ShiftBalanceTests
{
    private static Task<TestModuleDatabase<CashManagementDbContext>> NewDb()
        => TestModuleDatabase<CashManagementDbContext>.CreateAsync(s => s.AddCashManagementCore());

    private static async Task<Guid> OpenAsync(TestModuleDatabase<CashManagementDbContext> db, decimal floatAmount)
        => (await db.InScopeAsync(sp => sp.GetRequiredService<OpenCashSessionCommandHandler>().HandleAsync(new OpenCashSessionCommand("main", "ann", floatAmount)))).Value;

    private static Task<Platform.Core.Results.Result<RecordedCashMovement>> RecordAsync(
        TestModuleDatabase<CashManagementDbContext> db, Guid session, CashMovementKind kind, decimal amount, Guid? sale = null, string? reason = null)
        => db.InScopeAsync(sp => sp.GetRequiredService<RecordCashMovementCommandHandler>()
            .HandleAsync(new RecordCashMovementCommand(session, kind, amount, reason, sale is null ? null : "sale", sale, "ann")));

    [Fact]
    public async Task A_busy_shift_knows_its_balance_without_reading_its_movements()
    {
        await using var db = await NewDb();
        var session = await OpenAsync(db, 50m);
        var expected = 50m;
        for (var i = 1; i <= 300; i++)
        {
            var amount = Math.Round(i * 0.37m + 0.05m, 2);
            var recorded = await RecordAsync(db, session, CashMovementKind.CashSale, amount, Guid.NewGuid());
            expected += amount;
            Assert.True(recorded.IsSuccess, recorded.IsFailure ? recorded.Error.ToString() : null);
            Assert.Equal(expected, recorded.Value.BalanceAfter);
        }

        Assert.True((await RecordAsync(db, session, CashMovementKind.PayOut, 100m, reason: "supplier")).IsSuccess);
        expected -= 100m;

        // the full read (the Cash drawer screen) and the light read (another module before a sale) agree, to the cent
        var full = await db.InScopeAsync(sp => sp.GetRequiredService<GetCashSessionQueryHandler>().HandleAsync(new GetCashSessionQuery(session)));
        Assert.Equal(expected, full!.Balance);
        Assert.Equal(301, full.Movements.Count);
        Assert.Equal(full.Movements.Sum(m => m.SignedAmount), full.Balance - 50m);

        var light = await db.InScopeAsync(sp => sp.GetRequiredService<ICashSessionRepository>().GetByIdAsync(new CashSessionId(session), default, includeMovements: false));
        Assert.Empty(light!.Movements);
        Assert.Equal(expected, light.Balance);

        var contract = await db.InScopeAsync(sp => sp.GetRequiredService<ICashSessionReader>().GetOpenSessionAsync("MAIN"));
        Assert.Equal(expected, contract!.Balance);

        // closing compares the count with the same balance
        var closed = await db.InScopeAsync(sp => sp.GetRequiredService<CloseCashSessionCommandHandler>().HandleAsync(new CloseCashSessionCommand(session, expected, "ann")));
        Assert.Equal(0m, closed.Value.Variance);
        Assert.Equal(expected, closed.Value.ExpectedAmount);
    }

    [Fact]
    public async Task Recording_one_more_cash_sale_reads_the_shift_but_none_of_its_earlier_movements()
    {
        await using var db = await NewDb();
        var session = await OpenAsync(db, 50m);
        for (var i = 0; i < 200; i++)
            Assert.True((await RecordAsync(db, session, CashMovementKind.CashSale, 1m, Guid.NewGuid())).IsSuccess);

        using var scope = db.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ICashSessionReader>();
        Assert.Equal(250m, (await reader.GetOpenSessionAsync("MAIN"))!.Balance);
        var recorded = await scope.ServiceProvider.GetRequiredService<RecordCashMovementCommandHandler>()
            .HandleAsync(new RecordCashMovementCommand(session, CashMovementKind.CashSale, 1m, null, "sale", Guid.NewGuid(), "ann"));

        Assert.Equal(251m, recorded.Value.BalanceAfter);
        // what a checkout's scope holds of the cash module: the shift and the one new movement - not the 200 before it
        var tracked = scope.ServiceProvider.GetRequiredService<CashManagementDbContext>().ChangeTracker.Entries().ToList();
        Assert.Equal(2, tracked.Count);
    }

    [Fact]
    public async Task A_sale_is_counted_once_even_though_the_shift_is_read_without_its_history()
    {
        await using var db = await NewDb();
        var session = await OpenAsync(db, 20m);
        var sale = Guid.NewGuid();

        Assert.True((await RecordAsync(db, session, CashMovementKind.CashSale, 7.5m, sale)).IsSuccess);
        var again = await RecordAsync(db, session, CashMovementKind.CashSale, 7.5m, sale);
        Assert.Equal("CashManagement.Movement.DuplicateReference", again.Error.Code);
        Assert.True((await RecordAsync(db, session, CashMovementKind.CashRefund, 7.5m, sale)).IsSuccess);   // another kind for the same sale is allowed

        var full = await db.InScopeAsync(sp => sp.GetRequiredService<GetCashSessionQueryHandler>().HandleAsync(new GetCashSessionQuery(session)));
        Assert.Equal(2, full!.Movements.Count);
        Assert.Equal(20m, full.Balance);
    }

    [Fact]
    public async Task The_database_itself_refuses_a_second_movement_for_the_same_reference()
    {
        await using var db = await NewDb();
        var session = await OpenAsync(db, 20m);
        var sale = Guid.NewGuid();
        Assert.True((await RecordAsync(db, session, CashMovementKind.CashSale, 5m, sale)).IsSuccess);

        // a writer that skipped the check (the shift read without its movements, so the shift itself cannot see the first one)
        var refused = await db.InScopeAsync(async sp =>
        {
            var shift = await sp.GetRequiredService<ICashSessionRepository>().GetByIdAsync(new CashSessionId(session), default, includeMovements: false);
            Assert.True(shift!.RecordMovement(CashMovementKind.CashSale, 5m, referenceType: "sale", referenceId: sale).IsSuccess);
            return await Record.ExceptionAsync(() => sp.GetRequiredService<CashManagementDbContext>().SaveChangesAsync());
        });

        Assert.IsType<DbUpdateException>(refused);
        var full = await db.InScopeAsync(sp => sp.GetRequiredService<GetCashSessionQueryHandler>().HandleAsync(new GetCashSessionQuery(session)));
        Assert.Single(full!.Movements);
        Assert.Equal(25m, full.Balance);
    }

    [Fact]
    public async Task A_pay_out_still_cannot_take_more_than_the_balance_kept_with_the_shift()
    {
        await using var db = await NewDb();
        var session = await OpenAsync(db, 10m);
        Assert.True((await RecordAsync(db, session, CashMovementKind.CashSale, 5m, Guid.NewGuid())).IsSuccess);

        Assert.Equal("CashManagement.Movement.InsufficientCash", (await RecordAsync(db, session, CashMovementKind.PayOut, 15.01m, reason: "too much")).Error.Code);
        var exact = await RecordAsync(db, session, CashMovementKind.PayOut, 15m, reason: "all of it");
        Assert.Equal(0m, exact.Value.BalanceAfter);
    }

    [Fact]
    public async Task An_upgraded_database_gets_the_total_of_the_shifts_it_already_holds()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CashManagementDbContext>().UseSqlite(connection).Options;
        await using (var before = new CashManagementDbContext(options))
            await before.GetService<IMigrator>().MigrateAsync("20261005123451_InitialCashManagementSchema");

        // a shift written by the previous version: float 50, sales 12.30 and 7.25, a pay-in 10, a pay-out 3.10, a refund 1.05
        var session = Guid.NewGuid();
        var rows = new (int Kind, string Amount)[] { (3, "12.30"), (3, "7.25"), (1, "10"), (2, "3.10"), (4, "1.05") };
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = $"""
                INSERT INTO cash_Sessions (Id, DrawerCode, OpenedBy, OpeningFloat, Status, OpenedAt) VALUES ('{session.ToString().ToUpperInvariant()}', 'MAIN', 'ann', '50.0', 1, '2026-10-01 08:00:00');
                {string.Concat(rows.Select((r, i) => $"INSERT INTO cash_Movements (Id, SessionId, Kind, Amount, RecordedAt) VALUES ('{Guid.NewGuid().ToString().ToUpperInvariant()}', '{session.ToString().ToUpperInvariant()}', {r.Kind}, '{r.Amount}', '2026-10-01 0{i + 1}:00:00');"))}
                """;
            await insert.ExecuteNonQueryAsync();
        }

        await using (var after = new CashManagementDbContext(options))
        {
            await after.Database.MigrateAsync();
            var shift = await after.CashSessions.AsNoTracking().SingleAsync();
            Assert.Equal(25.40m, shift.MovementTotal);   // 12.30 + 7.25 + 10 - 3.10 - 1.05
            Assert.Equal(75.40m, shift.Balance);
        }
    }
}
