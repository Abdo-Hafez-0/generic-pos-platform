using Audit.Application.Commands;
using Audit.Application.Queries;
using Audit.Application.Security;
using Audit.Contracts.Interfaces;
using Audit.Contracts.Models;
using Audit.Infrastructure.DependencyInjection;
using Audit.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Tests.Common;
using Tests.Common.Security;

namespace Audit.Tests.Security;

public sealed class AuditAuthorizationTests
{
    private static async Task<(TestModuleDatabase<AuditDbContext> Db, ScriptedAuthorizationService Auth)> StartAsync(params string[] allowed)
    {
        var auth = new ScriptedAuthorizationService(allowed);
        var db = await TestModuleDatabase<AuditDbContext>.CreateAsync(s =>
        {
            s.AddAuditCore();
            s.AddSingleton<IAuthorizationService>(auth);
        });
        return (db, auth);
    }

    private static async Task<Guid> RecordAsync(TestModuleDatabase<AuditDbContext> db)
    {
        var result = await db.InScopeAsync(sp => sp.GetRequiredService<IAuditRecorder>().RecordAsync(new AuditRecordRequest("security", "test.event", "user", "u-1")));
        Assert.True(result.IsSuccess);
        return result.EntryId;
    }

    [Fact]
    public async Task Reading_the_audit_trail_through_the_handlers_needs_audit_view()
    {
        var (db, auth) = await StartAsync();
        await using var _ = db;
        var entry = await RecordAsync(db);
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var page = await sp.GetRequiredService<QueryAuditEntriesQueryHandler>().HandleAsync(new QueryAuditEntriesQuery());
        var one = await sp.GetRequiredService<GetAuditEntryQueryHandler>().HandleAsync(new GetAuditEntryQuery(entry));

        Assert.Equal(SecurityErrors.ForbiddenCode, page.Error.Code);
        Assert.Equal(SecurityErrors.ForbiddenCode, one.Error.Code);
        Assert.All(auth.Asked, c => Assert.Equal(AuditCapabilities.ViewAudit, c));

        auth.Allowed.Add(AuditCapabilities.ViewAudit);
        Assert.Equal(1, (await sp.GetRequiredService<QueryAuditEntriesQueryHandler>().HandleAsync(new QueryAuditEntriesQuery())).Value.TotalCount);
        Assert.NotNull((await sp.GetRequiredService<GetAuditEntryQueryHandler>().HandleAsync(new GetAuditEntryQuery(entry))).Value);
    }

    [Fact]
    public async Task Recording_to_the_audit_trail_needs_no_user_permission_because_modules_record_on_their_own_account()
    {
        var (db, auth) = await StartAsync();
        await using var _ = db;

        await RecordAsync(db);

        Assert.Empty(auth.Asked);
    }

    [Fact]
    public async Task The_module_s_own_read_contract_runs_inside_the_caller_s_authorization()
    {
        var (db, auth) = await StartAsync();
        await using var _ = db;
        await RecordAsync(db);

        var page = await db.InScopeAsync(sp => sp.GetRequiredService<IAuditReader>().QueryAsync());

        Assert.Equal(1, page.TotalCount);
        Assert.Empty(auth.Asked);
    }

    [Fact]
    public void Audit_view_is_sensitive_and_available_in_every_license_state()
    {
        var catalog = new CapabilityCatalog([new AuditCapabilityProvider()]);
        var view = catalog.Find(AuditCapabilities.ViewAudit)!;

        Assert.True(view.IsSensitive);
        Assert.Equal(LicenseRequirement.None, view.License);
        Assert.Equal("audit", view.Module);
    }
}
