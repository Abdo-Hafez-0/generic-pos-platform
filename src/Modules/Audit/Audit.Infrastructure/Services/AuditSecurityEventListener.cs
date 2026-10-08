using System.Collections.Concurrent;
using Audit.Contracts.Interfaces;
using Audit.Contracts.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Auditing;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Security;

namespace Audit.Infrastructure.Services;

/// <summary>
/// The durable home of security events: every <see cref="SecurityEvent"/> the platform produces (sign-ins, denials, password changes, license
/// and update verdicts, first-run setup...) becomes an append-only audit entry under the module name "security".
///
/// Best effort by contract: recording never throws into the audited operation. Events that arrive before the audit database exists (licensing
/// and the updater start before the module migrations run) or while it is unavailable wait in a small bounded buffer and are written, in order,
/// as soon as it works - the audit initializer drains it right after the migration. An event the audit rules reject outright (they validate
/// lengths) is dropped with a log line rather than blocking the ones behind it. Events cannot carry secrets: SecurityEvent has no field for one
/// and sanitises its text.
///
/// FIX-05: business events (a completed sale, a void, a stock adjustment, a price change...) take the same path - one ordered buffer for both -
/// under the reporting module's name. They are reported after the action committed and stamped HERE, at that moment, with the signed-in user
/// and the time, so an entry written later from the buffer still names who did it and when.
/// </summary>
internal sealed class AuditSecurityEventListener(
    IServiceScopeFactory scopes,
    ILogger<AuditSecurityEventListener>? logger = null,
    bool waitForStore = false,
    ICurrentUser? currentUser = null,
    TimeProvider? timeProvider = null) : ISecurityEventListener, IBusinessEventSink
{
    // In the host the audit tables only exist once AuditDatabaseInitializer has run: until then events are only buffered, so a fresh start
    // does not log failing writes against missing tables. Without an initializer (tests) writes are attempted immediately.
    private volatile bool _storeReady = !waitForStore;

    public const int MaxBuffered = 500;

    private readonly ConcurrentQueue<AuditRecordRequest> _pending = new();
    private readonly SemaphoreSlim _drain = new(1, 1);

    public int PendingCount => _pending.Count;

    public Task OnEventAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
        => EnqueueAsync(ToRequest(securityEvent), cancellationToken);

    /// <summary>
    /// A business event (FIX-05), stamped now with the signed-in user and the time, then written in the BACKGROUND: the action never waits for
    /// the audit store, and an event reported while a business transaction still holds the database lock is written once it is released.
    /// Never throws.
    /// </summary>
    public Task RecordAsync(BusinessEvent businessEvent, CancellationToken cancellationToken = default)
    {
        var actorKnown = businessEvent.ActorId is not null || businessEvent.ActorName is not null;
        var signedIn = !actorKnown && currentUser is { IsAuthenticated: true } ? currentUser : null;
        Enqueue(new AuditRecordRequest(
            Module: businessEvent.Module,
            Action: businessEvent.Action,
            EntityType: businessEvent.EntityType,
            EntityId: businessEvent.EntityId,
            ActorId: actorKnown ? businessEvent.ActorId : signedIn?.UserId,
            ActorName: actorKnown ? businessEvent.ActorName : signedIn?.UserName,
            Summary: businessEvent.Summary,
            Details: businessEvent.Details,
            OccurredAt: (businessEvent.OccurredAt ?? (timeProvider ?? TimeProvider.System).GetUtcNow()).UtcDateTime));

        if (_storeReady) LastBackgroundDrain = Task.Run(() => DrainQuietlyAsync());
        return Task.CompletedTask;
    }

    /// <summary>The most recent background write of business events (tests wait for it).</summary>
    internal Task LastBackgroundDrain { get; private set; } = Task.CompletedTask;

    private async Task DrainQuietlyAsync()
    {
        try
        {
            await DrainAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Writing buffered audit events failed; they stay buffered.");
        }
    }

    private void Enqueue(AuditRecordRequest request)
    {
        _pending.Enqueue(request);
        while (_pending.Count > MaxBuffered && _pending.TryDequeue(out _))
            logger?.LogWarning("The audit buffer is full; the oldest event was dropped.");
    }

    private async Task EnqueueAsync(AuditRecordRequest request, CancellationToken cancellationToken)
    {
        Enqueue(request);
        try
        {
            if (_storeReady) await DrainAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // the event stays buffered and is written with the next one
        }
    }

    /// <summary>Called by the audit initializer once the audit tables exist; writes everything buffered so far.</summary>
    public Task MarkStoreReadyAsync(CancellationToken cancellationToken = default)
    {
        _storeReady = true;
        return DrainAsync(cancellationToken);
    }

    /// <summary>Writes the buffered events in order. Stops at the first one the audit store cannot take yet (it stays buffered).</summary>
    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        await _drain.WaitAsync(cancellationToken);
        try
        {
            while (_pending.TryPeek(out var next))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var result = await scope.ServiceProvider.GetRequiredService<IAuditRecorder>().RecordAsync(next, cancellationToken);
                    if (!result.IsSuccess)
                        logger?.LogWarning("An audit event ({Module} {Action}) was refused by the audit rules and dropped: {Code}", next.Module, next.Action, result.ErrorCode);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogDebug(ex, "The audit store is not ready; {Count} event(s) stay buffered.", _pending.Count);
                    return;
                }

                _pending.TryDequeue(out _);
            }
        }
        finally
        {
            _drain.Release();
        }
    }

    internal static AuditRecordRequest ToRequest(SecurityEvent e) => new(
        Module: "security",
        Action: e.Action,
        EntityType: e.SubjectType,
        EntityId: e.SubjectId,
        ActorId: e.ActorId,
        ActorName: e.ActorName,
        Summary: e.Summary,
        Details: "outcome=" + e.Outcome,
        OccurredAt: e.OccurredAt?.UtcDateTime);
}
