using CashManagement.Application.Commands;
using CashManagement.Application.Queries;
using CashManagement.Contracts.Interfaces;
using CashManagement.Contracts.Models;
using CashManagement.Domain.Enums;
using CashManagement.Infrastructure.DependencyInjection;
using CashManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tests.Common;

namespace CashManagement.Tests.Application;

public sealed class CashManagementApplicationTests
{
    private static Task<TestModuleDatabase<CashManagementDbContext>> NewDb()
        => TestModuleDatabase<CashManagementDbContext>.CreateAsync(s => s.AddCashManagementCore());

    private static async Task<Guid> Open(TestModuleDatabase<CashManagementDbContext> db, string drawer = "main", decimal floatAmount = 100m)
    {
        var r = await db.InScopeAsync(sp => sp.GetRequiredService<OpenCashSessionCommandHandler>().HandleAsync(new OpenCashSessionCommand(drawer, "ann", floatAmount)));
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
        return r.Value;
    }

    private static Task<Platform.Core.Results.Result<RecordedCashMovement>> Record(
        TestModuleDatabase<CashManagementDbContext> db, Guid session, CashMovementKind kind, decimal amount, string? reason = null, string? refType = null, Guid? refId = null)
        => db.InScopeAsync(sp => sp.GetRequiredService<RecordCashMovementCommandHandler>()
            .HandleAsync(new RecordCashMovementCommand(session, kind, amount, reason, refType, refId, "ann")));

    private static Task<CashManagement.Application.DTOs.CashSessionDto?> Get(TestModuleDatabase<CashManagementDbContext> db, Guid id)
        => db.InScopeAsync(sp => sp.GetRequiredService<GetCashSessionQueryHandler>().HandleAsync(new GetCashSessionQuery(id)));

    [Fact]
    public async Task Open_Persists_AndAnotherSessionCannotOpenOnTheSameDrawer()
    {
        await using var db = await NewDb();
        var id = await Open(db, "main", 50m);

        var dup = await db.InScopeAsync(sp => sp.GetRequiredService<OpenCashSessionCommandHandler>().HandleAsync(new OpenCashSessionCommand("MAIN", "bob", 10m)));
        var other = await db.InScopeAsync(sp => sp.GetRequiredService<OpenCashSessionCommandHandler>().HandleAsync(new OpenCashSessionCommand("back", "bob", 10m)));

        var dto = await Get(db, id);
        Assert.Equal("MAIN", dto!.DrawerCode);
        Assert.Equal(50m, dto.Balance);
        Assert.Equal(CashSessionStatus.Open, dto.Status);
        Assert.Equal("CashManagement.OpenSession.AlreadyOpen", dup.Error.Code);
        Assert.True(other.IsSuccess);
        Assert.Equal(2, await db.InScopeAsync(sp => sp.GetRequiredService<CashManagementDbContext>().CashSessions.CountAsync()));
    }

    [Fact]
    public async Task Open_InvalidInput_FailsAndPersistsNothing()
    {
        await using var db = await NewDb();

        var r = await db.InScopeAsync(sp => sp.GetRequiredService<OpenCashSessionCommandHandler>().HandleAsync(new OpenCashSessionCommand("main", "ann", -5m)));

        Assert.Equal("CashManagement.Session.FloatNegative", r.Error.Code);
        Assert.Equal(0, await db.InScopeAsync(sp => sp.GetRequiredService<CashManagementDbContext>().CashSessions.CountAsync()));
    }

    [Fact]
    public async Task TheDatabaseItselfRefusesTwoOpenSessionsOnOneDrawer()
    {
        await using var db = await NewDb();
        await Open(db, "main");

        // bypass the application check: the filtered unique index must still hold
        var act = () => db.InScopeAsync(async sp =>
        {
            var context = sp.GetRequiredService<CashManagementDbContext>();
            context.CashSessions.Add(CashManagement.Domain.Entities.CashSession.Open("main", "bob", 1m).Value);
            return await context.SaveChangesAsync();
        });

        await Assert.ThrowsAsync<DbUpdateException>(act);
    }

    [Fact]
    public async Task ADrawerCanReopenAfterItsSessionIsClosed()
    {
        await using var db = await NewDb();
        var first = await Open(db, "main");
        await db.InScopeAsync(sp => sp.GetRequiredService<CloseCashSessionCommandHandler>().HandleAsync(new CloseCashSessionCommand(first, 100m, "ann")));

        var second = await Open(db, "main");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Movements_Persist_AndReturnTheBalanceAfter()
    {
        await using var db = await NewDb();
        var id = await Open(db, floatAmount: 100m);

        var a = await Record(db, id, CashMovementKind.CashSale, 40m);
        var b = await Record(db, id, CashMovementKind.PayOut, 25m, "supplier");

        Assert.Equal(140m, a.Value.BalanceAfter);
        Assert.Equal(115m, b.Value.BalanceAfter);
        var dto = await Get(db, id);
        Assert.Equal(115m, dto!.Balance);
        Assert.Equal(2, dto.Movements.Count);
        Assert.Equal("supplier", dto.Movements.Single(m => m.Kind == CashMovementKind.PayOut).Reason);
    }

    [Fact]
    public async Task RecordMovement_FailuresAreReported_AndNothingIsSaved()
    {
        await using var db = await NewDb();
        var id = await Open(db, floatAmount: 10m);
        var sale = Guid.NewGuid();
        await Record(db, id, CashMovementKind.CashSale, 5m, refType: "sale", refId: sale);

        var missing = await Record(db, Guid.NewGuid(), CashMovementKind.CashSale, 1m);
        var dup = await Record(db, id, CashMovementKind.CashSale, 5m, refType: "sale", refId: sale);
        var insufficient = await Record(db, id, CashMovementKind.PayOut, 500m, "x");
        var noReason = await Record(db, id, CashMovementKind.PayIn, 5m);

        Assert.Equal("CashManagement.RecordMovement.SessionNotFound", missing.Error.Code);
        Assert.Equal("CashManagement.Movement.DuplicateReference", dup.Error.Code);
        Assert.Equal("CashManagement.Movement.InsufficientCash", insufficient.Error.Code);
        Assert.Equal("CashManagement.Movement.ReasonRequired", noReason.Error.Code);
        Assert.Equal(15m, (await Get(db, id))!.Balance);
        Assert.Single((await Get(db, id))!.Movements);
    }

    [Fact]
    public async Task Close_StoresTheVariance_AndBlocksFurtherChanges()
    {
        await using var db = await NewDb();
        var id = await Open(db, floatAmount: 100m);
        await Record(db, id, CashMovementKind.CashSale, 50m);

        var closed = await db.InScopeAsync(sp => sp.GetRequiredService<CloseCashSessionCommandHandler>().HandleAsync(new CloseCashSessionCommand(id, 148m, "bob", "short")));
        var again = await db.InScopeAsync(sp => sp.GetRequiredService<CloseCashSessionCommandHandler>().HandleAsync(new CloseCashSessionCommand(id, 148m, "bob")));
        var movement = await Record(db, id, CashMovementKind.CashSale, 1m);
        var missing = await db.InScopeAsync(sp => sp.GetRequiredService<CloseCashSessionCommandHandler>().HandleAsync(new CloseCashSessionCommand(Guid.NewGuid(), 1m, "bob")));

        Assert.Equal(150m, closed.Value.ExpectedAmount);
        Assert.Equal(-2m, closed.Value.Variance);
        var dto = await Get(db, id);
        Assert.Equal(CashSessionStatus.Closed, dto!.Status);
        Assert.Equal(-2m, dto.Variance);
        Assert.Equal(148m, dto.CountedAmount);
        Assert.Equal("bob", dto.ClosedBy);
        Assert.Equal("CashManagement.Session.NotOpen", again.Error.Code);
        Assert.Equal("CashManagement.Session.NotOpen", movement.Error.Code);
        Assert.Equal("CashManagement.CloseSession.SessionNotFound", missing.Error.Code);
    }

    [Fact]
    public async Task GetOpenSession_FindsOnlyTheOpenOne_CaseInsensitively()
    {
        await using var db = await NewDb();
        var id = await Open(db, "main");

        var open = await db.InScopeAsync(sp => sp.GetRequiredService<GetOpenCashSessionQueryHandler>().HandleAsync(new GetOpenCashSessionQuery("Main")));
        await db.InScopeAsync(sp => sp.GetRequiredService<CloseCashSessionCommandHandler>().HandleAsync(new CloseCashSessionCommand(id, 100m, "ann")));
        var closed = await db.InScopeAsync(sp => sp.GetRequiredService<GetOpenCashSessionQueryHandler>().HandleAsync(new GetOpenCashSessionQuery("main")));
        var blank = await db.InScopeAsync(sp => sp.GetRequiredService<GetOpenCashSessionQueryHandler>().HandleAsync(new GetOpenCashSessionQuery(" ")));

        Assert.Equal(id, open!.SessionId);
        Assert.Null(closed);
        Assert.Null(blank);
        Assert.Null(await Get(db, Guid.NewGuid()));
    }

    [Fact]
    public async Task ListSessions_FiltersByDrawerAndStatus_NewestFirst_AndPages()
    {
        await using var db = await NewDb();
        var a = await Open(db, "main");
        await db.InScopeAsync(sp => sp.GetRequiredService<CloseCashSessionCommandHandler>().HandleAsync(new CloseCashSessionCommand(a, 100m, "ann")));
        await Task.Delay(5);
        await Open(db, "main");
        await Task.Delay(5);
        await Open(db, "back");

        var all = await db.InScopeAsync(sp => sp.GetRequiredService<ListCashSessionsQueryHandler>().HandleAsync(new ListCashSessionsQuery()));
        var main = await db.InScopeAsync(sp => sp.GetRequiredService<ListCashSessionsQueryHandler>().HandleAsync(new ListCashSessionsQuery("main")));
        var closed = await db.InScopeAsync(sp => sp.GetRequiredService<ListCashSessionsQueryHandler>().HandleAsync(new ListCashSessionsQuery(Status: CashSessionStatus.Closed)));
        var paged = await db.InScopeAsync(sp => sp.GetRequiredService<ListCashSessionsQueryHandler>().HandleAsync(new ListCashSessionsQuery(Page: 2, PageSize: 2)));
        var clamped = await db.InScopeAsync(sp => sp.GetRequiredService<ListCashSessionsQueryHandler>().HandleAsync(new ListCashSessionsQuery(Page: -1, PageSize: 9999)));

        Assert.Equal(3, all.TotalCount);
        Assert.Equal("BACK", all.Items[0].DrawerCode);
        Assert.Equal(2, main.TotalCount);
        Assert.Equal([a], closed.Items.Select(s => s.SessionId).ToArray());
        Assert.Single(paged.Items);
        Assert.Equal(1, clamped.Page);
        Assert.Equal(ListCashSessionsQueryHandler.MaxPageSize, clamped.PageSize);
    }

    // ------------------------------------------------------------------ contracts

    [Fact]
    public async Task Contract_Recorder_RecordsAndTranslatesFailures()
    {
        await using var db = await NewDb();
        var id = await Open(db, floatAmount: 20m);
        var sale = Guid.NewGuid();

        var ok = await db.InScopeAsync(sp => sp.GetRequiredService<ICashMovementRecorder>().RecordMovementAsync(
            new RecordCashMovementRequest(id, CashMovementKindContract.CashSale, 30m, null, "sale", sale, "pos")));
        var retry = await db.InScopeAsync(sp => sp.GetRequiredService<ICashMovementRecorder>().RecordMovementAsync(
            new RecordCashMovementRequest(id, CashMovementKindContract.CashSale, 30m, null, "sale", sale, "pos")));
        var unknown = await db.InScopeAsync(sp => sp.GetRequiredService<ICashMovementRecorder>().RecordMovementAsync(
            new RecordCashMovementRequest(Guid.NewGuid(), CashMovementKindContract.CashSale, 1m)));

        Assert.True(ok.IsSuccess);
        Assert.Equal(50m, ok.BalanceAfter);
        Assert.NotEqual(Guid.Empty, ok.MovementId);
        Assert.False(retry.IsSuccess);
        Assert.Equal("CashManagement.Movement.DuplicateReference", retry.ErrorCode);
        Assert.Equal(Guid.Empty, retry.MovementId);
        Assert.Equal("CashManagement.RecordMovement.SessionNotFound", unknown.ErrorCode);
        Assert.Equal(50m, (await Get(db, id))!.Balance);
    }

    [Fact]
    public async Task Contract_Reader_ReturnsOpenAndClosedSessions()
    {
        await using var db = await NewDb();
        var id = await Open(db, "main", 70m);

        var open = await db.InScopeAsync(sp => sp.GetRequiredService<ICashSessionReader>().GetOpenSessionAsync("MAIN"));
        await db.InScopeAsync(sp => sp.GetRequiredService<CloseCashSessionCommandHandler>().HandleAsync(new CloseCashSessionCommand(id, 70m, "ann")));
        var byId = await db.InScopeAsync(sp => sp.GetRequiredService<ICashSessionReader>().GetSessionAsync(id));
        var none = await db.InScopeAsync(sp => sp.GetRequiredService<ICashSessionReader>().GetOpenSessionAsync("main"));

        Assert.True(open!.IsOpen);
        Assert.Equal(70m, open.Balance);
        Assert.Equal(70m, open.OpeningFloat);
        Assert.False(byId!.IsOpen);
        Assert.Null(none);
    }

    [Fact]
    public async Task Contract_KindEnumMatchesTheDomain_AndNoDomainTypesLeak()
    {
        foreach (var name in Enum.GetNames<CashMovementKindContract>())
            Assert.Equal((int)Enum.Parse<CashMovementKind>(name), (int)Enum.Parse<CashMovementKindContract>(name));
        Assert.DoesNotContain(typeof(ICashMovementRecorder).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("CashManagement.Domain", StringComparison.Ordinal));
        Assert.False(RecordCashMovementResult.Failure("c", "m").IsSuccess);
        await Task.CompletedTask;
    }
}
