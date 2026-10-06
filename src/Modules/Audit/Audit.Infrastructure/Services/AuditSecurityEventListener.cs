using System.Collections.Concurrent;
using Audit.Contracts.Interfaces;
using Audit.Contracts.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// </summary>
internal sealed class AuditSecurityEventListener(
    IServiceScopeFactory scopes,
    ILogger<AuditSecurityEventListener>? logger = null,
    bool waitForStore = false) : ISecurityEventListener
{
    // In the host the audit tables only exist once AuditDatabaseInitializer has run: until then events are only buffered, so a fresh start
    // does not log failing writes against missing tables. Without an initializer (tests) writes are attempted immediately.
    private volatile bool _storeReady = !waitForStore;

    public const int MaxBuffered = 500;

    private readonly ConcurrentQueue<SecurityEvent> _pending = new();
    private readonly SemaphoreSlim _drain = new(1, 1);

    public int PendingCount => _pending.Count;

    public async Task OnEventAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
    {
        _pending.Enqueue(securityEvent);
        while (_pending.Count > MaxBuffered && _pending.TryDequeue(out _))
            logger?.LogWarning("The security-event buffer is full; the oldest event was dropped.");

        if (_storeReady) await DrainAsync(cancellationToken);
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
                    var result = await scope.ServiceProvider.GetRequiredService<IAuditRecorder>().RecordAsync(ToRequest(next), cancellationToken);
                    if (!result.IsSuccess)
                        logger?.LogWarning("A security event was refused by the audit rules and dropped: {Code}", result.ErrorCode);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogDebug(ex, "The audit store is not ready; {Count} security event(s) stay buffered.", _pending.Count);
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
