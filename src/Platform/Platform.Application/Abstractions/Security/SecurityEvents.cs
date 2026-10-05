using System.Text.RegularExpressions;

namespace Platform.Application.Abstractions.Security;

public enum SecurityEventOutcome
{
    Success = 0,
    Failure = 1,

    /// <summary>The action was refused by an authorization or license rule.</summary>
    Denied = 2
}

/// <summary>
/// Something security-relevant that happened (sign-in, password change, permission change, license decision, update verdict, backup
/// operation...). It has NO field for a secret by design, and its free text is sanitised on creation, so a caller cannot put a
/// password, key or token into the audit trail by accident.
/// </summary>
public sealed record SecurityEvent
{
    public const int MaxTextLength = 500;

    private SecurityEvent() { }

    /// <summary>Lower-case dotted verb, e.g. "security.signin.failed".</summary>
    public string Action { get; private init; } = string.Empty;

    public SecurityEventOutcome Outcome { get; private init; }

    public Guid? ActorId { get; private init; }

    public string? ActorName { get; private init; }

    /// <summary>What it happened to, e.g. "user", "capability", "license", "update".</summary>
    public string? SubjectType { get; private init; }

    public string? SubjectId { get; private init; }

    public string? Summary { get; private init; }

    public DateTimeOffset? OccurredAt { get; private init; }

    public static SecurityEvent Create(
        string action,
        SecurityEventOutcome outcome,
        Guid? actorId = null,
        string? actorName = null,
        string? subjectType = null,
        string? subjectId = null,
        string? summary = null,
        DateTimeOffset? occurredAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        return new SecurityEvent
        {
            Action = action.Trim().ToLowerInvariant(),
            Outcome = outcome,
            ActorId = actorId == Guid.Empty ? null : actorId,
            ActorName = SecretRedactor.Sanitize(actorName, 100),
            SubjectType = SecretRedactor.Sanitize(subjectType, 100)?.ToLowerInvariant(),
            SubjectId = SecretRedactor.Sanitize(subjectId, 100),
            Summary = SecretRedactor.Sanitize(summary, MaxTextLength),
            OccurredAt = occurredAt
        };
    }
}

/// <summary>
/// Where security events go. Recording is best effort: an implementation must not throw into the operation being audited, and a
/// missing sink never blocks a business operation. The Audit module provides the durable implementation when it is installed.
/// </summary>
public interface ISecurityEventSink
{
    Task RecordAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default);
}

/// <summary>Discards events (used when nothing records them).</summary>
public sealed class NullSecurityEventSink : ISecurityEventSink
{
    public Task RecordAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// Receives every security event (implemented by whatever records them: the Audit module, a logging listener...). Same contract as the
/// sink: best effort, never throws into the audited operation.
/// </summary>
public interface ISecurityEventListener
{
    Task OnEventAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// The sink every producer talks to: delivers each event to every registered <see cref="ISecurityEventListener"/>. One failing
/// listener never stops the others or the caller; with no listeners the event simply goes nowhere.
/// </summary>
public sealed class SecurityEventDispatcher(IEnumerable<ISecurityEventListener> listeners) : ISecurityEventSink
{
    private readonly ISecurityEventListener[] _listeners = listeners.ToArray();

    public async Task RecordAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
    {
        foreach (var listener in _listeners)
        {
            try
            {
                await listener.OnEventAsync(securityEvent, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Auditing is best effort by contract; the audited operation has already been decided.
            }
        }
    }
}

/// <summary>
/// Removes things that look like secrets from free text before it is stored or logged: tokens and keys of the platform's own
/// formats, PEM blocks, and "password=..." style assignments. It is a safety net; the first defence is not passing secrets at all.
/// </summary>
public static partial class SecretRedactor
{
    public const string Redacted = "[redacted]";

    // gpa_ admin keys, gpb_ backup tokens (Base64url body)
    [GeneratedRegex(@"\bgp[ab]_[A-Za-z0-9_\-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex PlatformToken();

    // activation key: 5 groups of 5 characters from the unambiguous alphabet
    [GeneratedRegex(@"\b[A-HJ-NP-Z2-9]{5}(?:-[A-HJ-NP-Z2-9]{5}){4}\b")]
    private static partial Regex ActivationKey();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*-----.*?(?:-----END [A-Z ]*-----|$)", RegexOptions.Singleline)]
    private static partial Regex PemBlock();

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|token|api[_\-]?key|authorization|private[_\-]?key|pin|key)\b(\s*[:=]\s*)(\S+)")]
    private static partial Regex Assignment();

    [GeneratedRegex(@"(?i)\b(bearer)(\s+)(\S+)")]
    private static partial Regex BearerValue();

    [GeneratedRegex(@"[\p{Cc}]")]
    private static partial Regex ControlCharacters();

    /// <summary>Redacts secret-looking content, strips control characters and truncates. Returns null for blank input.</summary>
    public static string? Sanitize(string? text, int maxLength = SecurityEvent.MaxTextLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var clean = ControlCharacters().Replace(text, " ");
        clean = PemBlock().Replace(clean, Redacted);
        clean = PlatformToken().Replace(clean, Redacted);
        clean = ActivationKey().Replace(clean, Redacted);
        clean = BearerValue().Replace(clean, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{Redacted}");
        clean = Assignment().Replace(clean, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{Redacted}");
        clean = clean.Trim();

        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }
}
