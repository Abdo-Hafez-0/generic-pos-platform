using Platform.Application.Abstractions.Security;

namespace Platform.Application.Abstractions.Auditing;

/// <summary>
/// A business action that belongs in the audit log (FIX-05): a completed sale, a void, a stock adjustment, a price change, a purchase receipt,
/// a cash movement... Reported by the module that performed it, AFTER the action committed. Plain values only, so a module needs no
/// reference to the Audit module; free text is sanitised and cut to the audit log's limits on creation, so an entry can never be refused
/// for its length and cannot carry a secret by accident.
/// </summary>
public sealed record BusinessEvent
{
    private BusinessEvent() { }

    /// <summary>The module that performed the action, e.g. "pos", "inventory".</summary>
    public string Module { get; private init; } = string.Empty;

    /// <summary>Lower-case dotted verb, e.g. "sale.completed", "stock.adjusted".</summary>
    public string Action { get; private init; } = string.Empty;

    /// <summary>What it happened to, e.g. "sale", "product", "cash-session".</summary>
    public string? EntityType { get; private init; }

    public string? EntityId { get; private init; }

    /// <summary>One sentence for a person reading the log, e.g. "Sale of 3 item(s), total 7.50, paid in cash."</summary>
    public string? Summary { get; private init; }

    /// <summary>Optional machine-friendly detail, e.g. "quantity=-2;reason=Damaged".</summary>
    public string? Details { get; private init; }

    /// <summary>Who did it. Normally left empty: the recorder stamps the signed-in user at the moment the event is reported.</summary>
    public Guid? ActorId { get; init; }

    public string? ActorName { get; init; }

    /// <summary>When it happened. Normally left empty: the recorder stamps the time the event is reported.</summary>
    public DateTimeOffset? OccurredAt { get; init; }

    public static BusinessEvent Create(string module, string action, string? entityType = null, string? entityId = null, string? summary = null, string? details = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        return new BusinessEvent
        {
            Module = Cut(module.Trim().ToLowerInvariant(), 50),
            Action = Cut(action.Trim().ToLowerInvariant(), 100),
            EntityType = SecretRedactor.Sanitize(entityType, 100)?.ToLowerInvariant(),
            EntityId = SecretRedactor.Sanitize(entityId, 100),
            Summary = SecretRedactor.Sanitize(summary, 500),
            Details = SecretRedactor.Sanitize(details, 4000)
        };
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>
/// Where business events go (the Audit module when installed). Best effort by contract: an implementation never throws into the caller and
/// never undoes or blocks the action, which has already been committed; it keeps an event it cannot store yet and writes it later.
/// </summary>
public interface IBusinessEventSink
{
    Task RecordAsync(BusinessEvent businessEvent, CancellationToken cancellationToken = default);
}

/// <summary>Lets a module that treats auditing as optional report without a null check (no Audit module = nothing recorded).</summary>
public static class BusinessEventSinkExtensions
{
    public static async Task TryRecordAsync(this IBusinessEventSink? sink, BusinessEvent businessEvent, CancellationToken cancellationToken = default)
    {
        if (sink is null) return;
        try
        {
            // The action is committed: the caller's cancellation must not lose its audit entry.
            await sink.RecordAsync(businessEvent, CancellationToken.None);
        }
        catch (Exception)
        {
            // Best effort by contract; a faulty sink must never turn a committed action into a reported failure.
        }
    }
}
