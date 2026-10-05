using Audit.Application.Commands;
using Audit.Application.Queries;
using Audit.Contracts.Interfaces;
using Audit.Contracts.Models;
using Audit.Infrastructure.DependencyInjection;
using Audit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tests.Common;

namespace Audit.Tests.Application;

public sealed class AuditApplicationTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private static Task<TestModuleDatabase<AuditDbContext>> NewDb()
        => TestModuleDatabase<AuditDbContext>.CreateAsync(s => s.AddAuditCore());

    private static async Task<Guid> Record(
        TestModuleDatabase<AuditDbContext> db, string module = "sales", string action = "sale.completed", string? type = "sale",
        string? id = "S-1", Guid? actor = null, int minutes = 0)
    {
        var r = await db.InScopeAsync(sp => sp.GetRequiredService<IAuditRecorder>()
            .RecordAsync(new AuditRecordRequest(module, action, type, id, actor, OccurredAt: T0.AddMinutes(minutes))));
        Assert.True(r.IsSuccess, r.ErrorMessage);
        return r.EntryId;
    }

    private static Task<AuditPage> Query(TestModuleDatabase<AuditDbContext> db, AuditEntryFilter? filter = null, int page = 1, int size = 50)
        => db.InScopeAsync(sp => sp.GetRequiredService<IAuditReader>().QueryAsync(filter, page, size));

    [Fact]
    public async Task Record_Persists_AndCanBeReadBack()
    {
        await using var db = await NewDb();
        var actor = Guid.NewGuid();
        var id = await db.InScopeAsync(async sp =>
        {
            var r = await sp.GetRequiredService<RecordAuditEntryCommandHandler>().HandleAsync(new RecordAuditEntryCommand(
                "Inventory", "Stock.Adjusted", "Product", "P-9", actor, "Ann", "Adjusted -3", "{}", T0));
            Assert.True(r.IsSuccess);
            return r.Value;
        });

        var dto = (await db.InScopeAsync(sp => sp.GetRequiredService<GetAuditEntryQueryHandler>().HandleAsync(new GetAuditEntryQuery(id)))).Value;

        Assert.Equal("inventory", dto!.Module);
        Assert.Equal("stock.adjusted", dto.Action);
        Assert.Equal("product", dto.EntityType);
        Assert.Equal("P-9", dto.EntityId);
        Assert.Equal(actor, dto.ActorId);
        Assert.Equal("Ann", dto.ActorName);
        Assert.Equal(T0, dto.OccurredAt);
        Assert.Null((await db.InScopeAsync(sp => sp.GetRequiredService<GetAuditEntryQueryHandler>().HandleAsync(new GetAuditEntryQuery(Guid.NewGuid())))).Value);
    }

    [Fact]
    public async Task Record_Invalid_FailsAndPersistsNothing()
    {
        await using var db = await NewDb();

        var r = await db.InScopeAsync(sp => sp.GetRequiredService<RecordAuditEntryCommandHandler>().HandleAsync(new RecordAuditEntryCommand(" ", "x")));

        Assert.Equal("Audit.Entry.ModuleRequired", r.Error.Code);
        Assert.Equal(0, await db.InScopeAsync(sp => sp.GetRequiredService<AuditDbContext>().AuditEntries.CountAsync()));
    }

    [Fact]
    public async Task Query_ReturnsNewestFirst_AndPages()
    {
        await using var db = await NewDb();
        for (var i = 0; i < 5; i++) await Record(db, id: $"S-{i}", minutes: i);

        var first = await Query(db, size: 2);
        var third = await Query(db, page: 3, size: 2);

        Assert.Equal(5, first.TotalCount);
        Assert.Equal(["S-4", "S-3"], first.Items.Select(e => e.EntityId!).ToArray());
        Assert.Equal(["S-0"], third.Items.Select(e => e.EntityId!).ToArray());
    }

    [Fact]
    public async Task Query_ClampsPaging()
    {
        await using var db = await NewDb();
        await Record(db);

        var page = await Query(db, page: -3, size: 100000);

        Assert.Equal(1, page.Page);
        Assert.Equal(QueryAuditEntriesQueryHandler.MaxPageSize, page.PageSize);
        Assert.Equal(1, (await Query(db, size: 0)).PageSize);
    }

    [Fact]
    public async Task Query_FiltersCombineWithAnd_AndIgnoreCaseWhereDocumented()
    {
        await using var db = await NewDb();
        var actor = Guid.NewGuid();
        await Record(db, "sales", "sale.completed", "sale", "S-1", actor, 1);
        await Record(db, "sales", "sale.cancelled", "sale", "S-2", null, 2);
        await Record(db, "inventory", "stock.adjusted", "product", "P-1", actor, 3);

        Assert.Equal(2, (await Query(db, new AuditEntryFilter(Module: "SALES"))).TotalCount);
        Assert.Equal(1, (await Query(db, new AuditEntryFilter(Module: "sales", Action: "Sale.Cancelled"))).TotalCount);
        Assert.Equal(1, (await Query(db, new AuditEntryFilter(EntityType: "PRODUCT"))).TotalCount);
        Assert.Equal(1, (await Query(db, new AuditEntryFilter(EntityId: "S-2"))).TotalCount);
        Assert.Equal(2, (await Query(db, new AuditEntryFilter(ActorId: actor))).TotalCount);
        Assert.Equal(1, (await Query(db, new AuditEntryFilter(Module: "sales", ActorId: actor))).TotalCount);
        Assert.Equal(0, (await Query(db, new AuditEntryFilter(Module: "payments"))).TotalCount);
        Assert.Equal(3, (await Query(db, new AuditEntryFilter(Module: " "))).TotalCount);   // blank filter = no filter
    }

    [Fact]
    public async Task Query_FiltersByInclusiveTimeRange()
    {
        await using var db = await NewDb();
        for (var i = 0; i < 4; i++) await Record(db, id: $"S-{i}", minutes: i * 10);   // 0,10,20,30 min

        var range = await Query(db, new AuditEntryFilter(From: T0.AddMinutes(10), To: T0.AddMinutes(20)));
        var from = await Query(db, new AuditEntryFilter(From: T0.AddMinutes(25)));
        var to = await Query(db, new AuditEntryFilter(To: T0.AddMinutes(5)));

        Assert.Equal(["S-2", "S-1"], range.Items.Select(e => e.EntityId!).ToArray());
        Assert.Equal(["S-3"], from.Items.Select(e => e.EntityId!).ToArray());
        Assert.Equal(["S-0"], to.Items.Select(e => e.EntityId!).ToArray());
    }

    // ------------------------------------------------------------------ contracts

    [Fact]
    public async Task Contract_Recorder_TranslatesSuccessAndFailure()
    {
        await using var db = await NewDb();

        var ok = await db.InScopeAsync(sp => sp.GetRequiredService<IAuditRecorder>().RecordAsync(new AuditRecordRequest("sales", "sale.completed")));
        var bad = await db.InScopeAsync(sp => sp.GetRequiredService<IAuditRecorder>().RecordAsync(new AuditRecordRequest("", "x")));

        Assert.True(ok.IsSuccess);
        Assert.NotEqual(Guid.Empty, ok.EntryId);
        Assert.False(bad.IsSuccess);
        Assert.Equal(Guid.Empty, bad.EntryId);
        Assert.Equal("Audit.Entry.ModuleRequired", bad.ErrorCode);
        Assert.Equal(1, (await Query(db)).TotalCount);
    }

    [Fact]
    public async Task Contract_Reader_GetReturnsTheEntry_OrNull()
    {
        await using var db = await NewDb();
        var id = await Record(db);

        var found = await db.InScopeAsync(sp => sp.GetRequiredService<IAuditReader>().GetAsync(id));
        var missing = await db.InScopeAsync(sp => sp.GetRequiredService<IAuditReader>().GetAsync(Guid.NewGuid()));

        Assert.Equal("sales", found!.Module);
        Assert.Null(missing);
    }

    [Fact]
    public async Task Contract_ExposesNoMutation_AndNoDomainTypes()
    {
        Assert.Equal(["RecordAsync"], typeof(IAuditRecorder).GetMethods().Select(m => m.Name).ToArray());
        Assert.Equal(["GetAsync", "QueryAsync"], typeof(IAuditReader).GetMethods().Select(m => m.Name).OrderBy(n => n).ToArray());
        Assert.DoesNotContain(typeof(IAuditRecorder).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Audit.Domain", StringComparison.Ordinal));
        await Task.CompletedTask;
    }
}
