using Audit.Contracts.Interfaces;
using Audit.Contracts.Models;
using Audit.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Auditing;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Security;

namespace Audit.Tests.Security;

/// <summary>
/// FIX-05: business events reach the audit log through the same buffer as security events - stamped with the signed-in user and the time
/// when they are reported, written in the background, kept while the store is unavailable, never thrown into the action.
/// </summary>
public sealed class BusinessEventTests
{
    private sealed class Recorder : IAuditRecorder
    {
        public bool Down { get; set; }
        public List<AuditRecordRequest> Recorded { get; } = [];

        public Task<AuditRecordResult> RecordAsync(AuditRecordRequest request, CancellationToken cancellationToken = default)
        {
            if (Down) throw new InvalidOperationException("database is locked");
            lock (Recorded) Recorded.Add(request);
            return Task.FromResult(AuditRecordResult.Success(Guid.NewGuid()));
        }
    }

    private sealed class User : ICurrentUser
    {
        public bool IsAuthenticated { get; set; } = true;
        public Guid UserId { get; set; } = Guid.NewGuid();
        public string UserName { get; set; } = "cashier1";
        public string DisplayName { get; set; } = "First Cashier";
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Recorder _recorder = new();
    private readonly User _user = new();
    private readonly Clock _clock = new(new DateTimeOffset(2026, 10, 8, 9, 30, 0, TimeSpan.Zero));
    private readonly AuditSecurityEventListener _sink;

    public BusinessEventTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuditRecorder>(_recorder);
        var provider = services.BuildServiceProvider();
        _sink = new AuditSecurityEventListener(provider.GetRequiredService<IServiceScopeFactory>(), currentUser: _user, timeProvider: _clock);
    }

    private async Task Written() => await _sink.LastBackgroundDrain;

    [Fact]
    public async Task A_business_event_is_written_under_its_module_with_the_user_and_time_of_the_moment_it_was_reported()
    {
        await _sink.RecordAsync(BusinessEvent.Create("POS", "Sale.Completed", "Sale", "s-1", "Sale of 2 line(s), total 5.00, paid by cash.", "cart=c-1"));
        var reportedAt = _clock.Now;
        var reporter = _user.UserName;
        _clock.Now = _clock.Now.AddHours(1);      // the user and the time change before the entry is written: the entry keeps the moment
        _user.UserName = "someone-else";
        await Written();

        var entry = Assert.Single(_recorder.Recorded);
        Assert.Equal(("pos", "sale.completed", "sale", "s-1"), (entry.Module, entry.Action, entry.EntityType, entry.EntityId));
        Assert.Equal((reporter, reportedAt.UtcDateTime), (entry.ActorName, entry.OccurredAt));
        Assert.Equal("Sale of 2 line(s), total 5.00, paid by cash.", entry.Summary);
        Assert.Equal("cart=c-1", entry.Details);
    }

    [Fact]
    public async Task An_actor_given_by_the_module_is_kept_and_nobody_signed_in_leaves_the_actor_empty()
    {
        var actor = Guid.NewGuid();
        await _sink.RecordAsync(BusinessEvent.Create("inventory", "stock.adjusted") with { ActorId = actor, ActorName = "service" });
        await Written();
        _user.IsAuthenticated = false;
        await _sink.RecordAsync(BusinessEvent.Create("inventory", "stock.adjusted"));
        await Written();

        Assert.Equal((actor, "service"), (_recorder.Recorded[0].ActorId, _recorder.Recorded[0].ActorName));
        Assert.Equal(((Guid?)null, (string?)null), (_recorder.Recorded[1].ActorId, _recorder.Recorded[1].ActorName));
    }

    [Fact]
    public async Task While_the_store_is_unavailable_events_wait_and_are_written_in_order_with_the_security_events()
    {
        _recorder.Down = true;
        await _sink.RecordAsync(BusinessEvent.Create("cash-management", "cash.pay-out"));
        await Written();
        await _sink.OnEventAsync(SecurityEvent.Create("security.signin.succeeded", SecurityEventOutcome.Success));
        Assert.Equal(2, _sink.PendingCount);

        _recorder.Down = false;
        await _sink.RecordAsync(BusinessEvent.Create("cash-management", "cash.shift-closed"));
        await Written();

        Assert.Equal(["cash.pay-out", "security.signin.succeeded", "cash.shift-closed"], _recorder.Recorded.Select(r => r.Action));
        Assert.Equal(0, _sink.PendingCount);
    }

    [Fact]
    public async Task Reporting_never_waits_for_the_store_and_never_throws()
    {
        _recorder.Down = true;

        var reported = _sink.RecordAsync(BusinessEvent.Create("pricing", "price.changed"));

        Assert.True(reported.IsCompletedSuccessfully);              // the action does not wait for the audit write
        await Written();
        Assert.Equal(1, _sink.PendingCount);
    }

    [Fact]
    public async Task Before_the_audit_store_exists_events_are_only_buffered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuditRecorder>(_recorder);
        var sink = new AuditSecurityEventListener(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), waitForStore: true, currentUser: _user);

        await sink.RecordAsync(BusinessEvent.Create("pos", "sale.completed"));
        Assert.Equal(1, sink.PendingCount);
        Assert.Empty(_recorder.Recorded);

        await sink.MarkStoreReadyAsync();
        Assert.Equal("sale.completed", Assert.Single(_recorder.Recorded).Action);
    }

    [Fact]
    public void A_business_event_is_cut_to_the_audit_limits_and_cannot_carry_a_secret()
    {
        var e = BusinessEvent.Create(new string('m', 80), new string('a', 150), "Product", new string('i', 150),
            "changed by admin password=Hunter2!x " + new string('s', 600), new string('d', 5000));

        Assert.Equal(50, e.Module.Length);
        Assert.Equal(100, e.Action.Length);
        Assert.Equal("product", e.EntityType);
        Assert.Equal(100, e.EntityId!.Length);
        Assert.Equal(500, e.Summary!.Length);
        Assert.DoesNotContain("Hunter2", e.Summary);
        Assert.Equal(4000, e.Details!.Length);
    }

    private sealed class Throwing : IBusinessEventSink
    {
        public Task RecordAsync(BusinessEvent businessEvent, CancellationToken cancellationToken = default) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task A_missing_or_faulty_sink_never_reaches_the_action()
    {
        await ((IBusinessEventSink?)null).TryRecordAsync(BusinessEvent.Create("pos", "sale.completed"));
        await new Throwing().TryRecordAsync(BusinessEvent.Create("pos", "sale.completed"));
    }
}
