using CashManagement.Application.Commands;
using CashManagement.Application.Security;
using CashManagement.Contracts.Interfaces;
using CashManagement.Contracts.Models;
using CashManagement.Infrastructure.DependencyInjection;
using CashManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Tests.Common;
using Tests.Common.Security;

namespace CashManagement.Tests.Security;

public sealed class CashManagementAuthorizationTests
{
    private static async Task<(TestModuleDatabase<CashManagementDbContext> Db, ScriptedAuthorizationService Auth)> StartAsync(params string[] allowed)
    {
        var auth = new ScriptedAuthorizationService(allowed);
        var db = await TestModuleDatabase<CashManagementDbContext>.CreateAsync(s =>
        {
            s.AddCashManagementCore();
            s.AddSingleton<IAuthorizationService>(auth);
        });
        return (db, auth);
    }

    private static Task<int> SessionCountAsync(TestModuleDatabase<CashManagementDbContext> db)
        => db.InScopeAsync(sp => sp.GetRequiredService<CashManagementDbContext>().CashSessions.CountAsync());

    private sealed class SignedIn(string userName) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => userName;
        public string DisplayName => userName;
    }

    [Fact]
    public async Task The_drawer_records_the_signed_in_user_whatever_name_the_caller_passes()
    {
        var auth = new ScriptedAuthorizationService(CashManagementCapabilities.ManageSessions, CashManagementCapabilities.RecordMovement);
        await using var db = await TestModuleDatabase<CashManagementDbContext>.CreateAsync(s =>
        {
            s.AddCashManagementCore();
            s.AddSingleton<IAuthorizationService>(auth);
            s.AddSingleton<ICurrentUser>(new SignedIn("ann"));
        });
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var id = (await sp.GetRequiredService<OpenCashSessionCommandHandler>().HandleAsync(new OpenCashSessionCommand("main", "bob", 100m))).Value;
        Assert.True((await sp.GetRequiredService<RecordCashMovementCommandHandler>().HandleAsync(
            new RecordCashMovementCommand(id, global::CashManagement.Domain.Enums.CashMovementKind.PayOut, 20m, "milk", RecordedBy: "bob"))).IsSuccess);
        Assert.True((await sp.GetRequiredService<CloseCashSessionCommandHandler>().HandleAsync(new CloseCashSessionCommand(id, 80m, "bob"))).IsSuccess);

        var session = await sp.GetRequiredService<global::CashManagement.Application.Queries.GetCashSessionQueryHandler>()
            .HandleAsync(new global::CashManagement.Application.Queries.GetCashSessionQuery(id));
        Assert.Equal(("ann", "ann", "ann"), (session!.OpenedBy, session.ClosedBy, Assert.Single(session.Movements).RecordedBy));
    }

    [Fact]
    public async Task Opening_closing_and_recording_cash_each_need_their_capability()
    {
        var (db, auth) = await StartAsync();
        await using var _ = db;
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var open = await sp.GetRequiredService<OpenCashSessionCommandHandler>().HandleAsync(new OpenCashSessionCommand("main", "ann", 100m));
        var close = await sp.GetRequiredService<CloseCashSessionCommandHandler>().HandleAsync(new CloseCashSessionCommand(Guid.NewGuid(), 100m, "ann"));
        var movement = await sp.GetRequiredService<RecordCashMovementCommandHandler>().HandleAsync(
            new RecordCashMovementCommand(Guid.NewGuid(), global::CashManagement.Domain.Enums.CashMovementKind.PayOut, 50m, "take the money"));

        Assert.Equal(SecurityErrors.ForbiddenCode, open.Error.Code);
        Assert.Equal(SecurityErrors.ForbiddenCode, close.Error.Code);
        Assert.Equal(SecurityErrors.ForbiddenCode, movement.Error.Code);
        Assert.Contains(CashManagementCapabilities.ManageSessions, auth.Asked);
        Assert.Contains(CashManagementCapabilities.RecordMovement, auth.Asked);
        Assert.Equal(0, await SessionCountAsync(db));
    }

    [Fact]
    public async Task With_the_capability_a_session_opens()
    {
        var (db, _) = await StartAsync(CashManagementCapabilities.ManageSessions);
        await using var _2 = db;
        using var scope = db.CreateScope();

        var open = await scope.ServiceProvider.GetRequiredService<OpenCashSessionCommandHandler>().HandleAsync(new OpenCashSessionCommand("main", "ann", 100m));

        Assert.True(open.IsSuccess, open.IsFailure ? open.Error.ToString() : null);
        Assert.Equal(1, await SessionCountAsync(db));
    }

    [Fact]
    public async Task The_contract_other_modules_use_does_not_ask_for_a_second_permission()
    {
        var (db, auth) = await StartAsync(CashManagementCapabilities.ManageSessions);
        await using var _ = db;
        Guid session;
        using (var scope = db.CreateScope())
            session = (await scope.ServiceProvider.GetRequiredService<OpenCashSessionCommandHandler>().HandleAsync(new OpenCashSessionCommand("main", "ann", 100m))).Value;
        auth.Asked.Clear();
        using var other = db.CreateScope();

        var result = await other.ServiceProvider.GetRequiredService<ICashMovementRecorder>().RecordMovementAsync(
            new RecordCashMovementRequest(session, CashMovementKindContract.CashSale, 12m, null, "sale", Guid.NewGuid(), "pos"));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Empty(auth.Asked);
    }

    [Fact]
    public void Every_capability_is_declared_once_owned_by_cash_management_and_sensitive()
    {
        var catalog = new CapabilityCatalog([new CashManagementCapabilityProvider()]);

        Assert.Equal(CashManagementCapabilities.All.Count, catalog.All.Count);
        Assert.All(catalog.All, c => Assert.Equal("cash-management", c.Module));
        Assert.All(catalog.All, c => Assert.True(c.IsSensitive));
    }
}
