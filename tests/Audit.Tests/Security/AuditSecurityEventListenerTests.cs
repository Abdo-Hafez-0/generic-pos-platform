using Audit.Contracts.Interfaces;
using Audit.Contracts.Models;
using Audit.Infrastructure.DependencyInjection;
using Audit.Infrastructure.Persistence;
using Audit.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Security;
using Tests.Common;

namespace Audit.Tests.Security;

public sealed class AuditSecurityEventListenerTests
{
    private static Task<TestModuleDatabase<AuditDbContext>> NewDb(Action<IServiceCollection>? extra = null)
        => TestModuleDatabase<AuditDbContext>.CreateAsync(s =>
        {
            s.AddAuditCore();
            s.AddSingleton<AuditSecurityEventListener>();
            extra?.Invoke(s);
        });

    private static AuditSecurityEventListener Listener(TestModuleDatabase<AuditDbContext> db)
        => db.InScopeAsync(sp => Task.FromResult(sp.GetRequiredService<AuditSecurityEventListener>())).GetAwaiter().GetResult();

    private static Task<AuditPage> SecurityEntries(TestModuleDatabase<AuditDbContext> db)
        => db.InScopeAsync(sp => sp.GetRequiredService<IAuditReader>().QueryAsync(new AuditEntryFilter(Module: "security"), pageSize: 200));

    [Fact]
    public async Task A_security_event_becomes_an_append_only_audit_entry_under_the_security_module()
    {
        await using var db = await NewDb();
        var actor = Guid.NewGuid();
        var when = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        await Listener(db).OnEventAsync(SecurityEvent.Create(
            "security.signin.failed", SecurityEventOutcome.Failure, actor, "ann", "user", actor.ToString(), "sign-in: wrong password", when));

        var entry = Assert.Single((await SecurityEntries(db)).Items);
        Assert.Equal("security", entry.Module);
        Assert.Equal("security.signin.failed", entry.Action);
        Assert.Equal("user", entry.EntityType);
        Assert.Equal(actor.ToString(), entry.EntityId);
        Assert.Equal(actor, entry.ActorId);
        Assert.Equal("ann", entry.ActorName);
        Assert.Equal("sign-in: wrong password", entry.Summary);
        Assert.Equal("outcome=Failure", entry.Details);
        Assert.Equal(when.UtcDateTime, entry.OccurredAt);
    }

    [Fact]
    public async Task Secrets_never_reach_the_audit_log_even_if_a_producer_tries()
    {
        await using var db = await NewDb();

        await Listener(db).OnEventAsync(SecurityEvent.Create(
            "security.test", SecurityEventOutcome.Failure,
            summary: "login failed password=Hunter2!hunter token=gpb_AbCdEfGhIjKlMnOpQrStUvWxYz012345 key ABCDE-FGHJK-LMNPQ-RSTUV-WXYZ2"));

        var entry = Assert.Single((await SecurityEntries(db)).Items);
        Assert.DoesNotContain("Hunter2", entry.Summary);
        Assert.DoesNotContain("AbCdEfGhIjKlMnOpQrStUvWxYz012345", entry.Summary);
        Assert.DoesNotContain("ABCDE-FGHJK", entry.Summary);
    }

    private sealed class FlakyRecorder : IAuditRecorder
    {
        public bool Down { get; set; } = true;
        public List<string> Recorded { get; } = [];

        public Task<AuditRecordResult> RecordAsync(AuditRecordRequest request, CancellationToken cancellationToken = default)
        {
            if (Down) throw new InvalidOperationException("no such table: aud_AuditEntries");
            Recorded.Add(request.Action);
            return Task.FromResult(AuditRecordResult.Success(Guid.NewGuid()));
        }
    }

    [Fact]
    public async Task Events_raised_before_the_audit_store_exists_wait_and_are_written_in_order_once_it_works()
    {
        var recorder = new FlakyRecorder();
        var services = new ServiceCollection();
        services.AddSingleton<IAuditRecorder>(recorder);
        services.AddSingleton<AuditSecurityEventListener>();
        await using var provider = services.BuildServiceProvider();
        var listener = provider.GetRequiredService<AuditSecurityEventListener>();

        await listener.OnEventAsync(SecurityEvent.Create("security.one", SecurityEventOutcome.Success));   // licensing starts before the migrations
        await listener.OnEventAsync(SecurityEvent.Create("security.two", SecurityEventOutcome.Success));
        Assert.Equal(2, listener.PendingCount);
        Assert.Empty(recorder.Recorded);

        recorder.Down = false;                                                                              // the audit initializer finished
        await listener.DrainAsync();

        Assert.Equal(["security.one", "security.two"], recorder.Recorded);
        Assert.Equal(0, listener.PendingCount);

        await listener.OnEventAsync(SecurityEvent.Create("security.three", SecurityEventOutcome.Success));
        Assert.Equal(["security.one", "security.two", "security.three"], recorder.Recorded);
    }

    [Fact]
    public async Task The_buffer_is_bounded_so_an_unavailable_store_cannot_exhaust_memory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuditRecorder>(new FlakyRecorder());
        services.AddSingleton<AuditSecurityEventListener>();
        await using var provider = services.BuildServiceProvider();
        var listener = provider.GetRequiredService<AuditSecurityEventListener>();

        for (var i = 0; i < AuditSecurityEventListener.MaxBuffered + 50; i++)
            await listener.OnEventAsync(SecurityEvent.Create("security.flood", SecurityEventOutcome.Denied));

        Assert.Equal(AuditSecurityEventListener.MaxBuffered, listener.PendingCount);
    }

    [Fact]
    public async Task An_event_the_audit_rules_refuse_is_dropped_and_does_not_block_the_ones_behind_it()
    {
        await using var db = await NewDb();
        var listener = Listener(db);

        // an over-long action passes SecurityEvent.Create (it only sanitises) but violates the audit entry rules
        await listener.OnEventAsync(SecurityEvent.Create(new string('a', 150), SecurityEventOutcome.Failure));
        await listener.OnEventAsync(SecurityEvent.Create("security.ok", SecurityEventOutcome.Success));

        Assert.Equal(0, listener.PendingCount);
        Assert.Equal("security.ok", Assert.Single((await SecurityEntries(db)).Items).Action);
    }
}
