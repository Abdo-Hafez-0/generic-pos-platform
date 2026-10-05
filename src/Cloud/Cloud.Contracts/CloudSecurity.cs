using System.Collections.Concurrent;

namespace Cloud.Contracts;

/// <summary>Writes security-relevant facts about server API access (not about vendor actions: those have the admin audit log).</summary>
public interface ICloudSecurityLog
{
    /// <summary>Best effort: a log that cannot be written must never turn an answered request into an error.</summary>
    Task RecordAsync(string actor, string action, string entityType, string entityId, string summary, CancellationToken cancellationToken = default);
}

/// <summary>Used when no durable store is configured (development): facts are simply not kept.</summary>
public sealed class NullCloudSecurityLog : ICloudSecurityLog
{
    public Task RecordAsync(string actor, string action, string entityType, string entityId, string summary, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>How many failed authentications a caller may make before it is turned away for a while.</summary>
/// <param name="MaxFailures">Failures inside <paramref name="Window"/> that trigger a block. Never below 3.</param>
/// <param name="Window">How long a failure counts.</param>
/// <param name="Lockout">How long a blocked caller is refused (every request, including a correct credential).</param>
public sealed record AuthenticationThrottleOptions(int MaxFailures = 10, TimeSpan? Window = null, TimeSpan? Lockout = null)
{
    public const int MaxFailuresFloor = 3;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DefaultLockout = TimeSpan.FromMinutes(15);

    public static AuthenticationThrottleOptions Default { get; } = new();

    public TimeSpan EffectiveWindow => Window is { } w && w > TimeSpan.Zero ? w : DefaultWindow;

    public TimeSpan EffectiveLockout => Lockout is { } l && l > TimeSpan.Zero ? l : DefaultLockout;

    /// <summary>Configuration can make the throttle stricter but never switch it off (floor of three failures, positive durations).</summary>
    public AuthenticationThrottleOptions Normalized() => new(Math.Max(MaxFailures, MaxFailuresFloor), EffectiveWindow, EffectiveLockout);
}

public readonly record struct ThrottleDecision(bool Allowed, TimeSpan RetryAfter)
{
    public static ThrottleDecision Allow { get; } = new(true, TimeSpan.Zero);
}

/// <summary>
/// Brute-force resistance for the credentials a server accepts (admin keys, backup tokens, activation keys): after too many failures from
/// one caller (the remote address) that caller is refused for a while, even with a correct credential. In memory per host: it protects against
/// guessing, not against a distributed attack, and behind a reverse proxy the host must be given the real client address (forwarded headers).
/// The credentials themselves are long random values (at least 125 bits), so guessing is infeasible; this makes it visibly noisy as well.
/// </summary>
public sealed class AuthenticationThrottle(AuthenticationThrottleOptions options, TimeProvider timeProvider)
{
    private const int MaxTrackedCallers = 10_000;

    private sealed class Entry
    {
        public readonly object Gate = new();
        public int Failures;
        public DateTimeOffset WindowStart;
        public DateTimeOffset? BlockedUntil;
    }

    private readonly AuthenticationThrottleOptions _options = options.Normalized();
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public ThrottleDecision Check(string caller)
    {
        if (!_entries.TryGetValue(caller, out var entry)) return ThrottleDecision.Allow;

        var now = timeProvider.GetUtcNow();
        lock (entry.Gate)
        {
            if (entry.BlockedUntil is { } until)
            {
                if (now < until) return new ThrottleDecision(false, until - now);
                entry.BlockedUntil = null;
                entry.Failures = 0;
            }

            return ThrottleDecision.Allow;
        }
    }

    /// <summary>Counts a failed attempt. Returns true when THIS failure blocked the caller.</summary>
    public bool RecordFailure(string caller)
    {
        var now = timeProvider.GetUtcNow();
        if (_entries.Count >= MaxTrackedCallers) Evict(now);

        var entry = _entries.GetOrAdd(caller, _ => new Entry { WindowStart = now });
        lock (entry.Gate)
        {
            if (entry.BlockedUntil is { } until && now < until) return false;

            if (now - entry.WindowStart > _options.EffectiveWindow)
            {
                entry.WindowStart = now;
                entry.Failures = 0;
                entry.BlockedUntil = null;
            }

            entry.Failures++;
            if (entry.Failures < _options.MaxFailures) return false;

            entry.BlockedUntil = now + _options.EffectiveLockout;
            return true;
        }
    }

    public void RecordSuccess(string caller) => _entries.TryRemove(caller, out _);

    private void Evict(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries)
        {
            lock (entry.Gate)
            {
                var stale = entry.BlockedUntil is null ? now - entry.WindowStart > _options.EffectiveWindow : now > entry.BlockedUntil;
                if (stale) _entries.TryRemove(key, out _);
            }
        }

        // still full of live entries: drop the oldest quarter rather than grow without bound
        if (_entries.Count >= MaxTrackedCallers)
            foreach (var key in _entries.OrderBy(e => e.Value.WindowStart).Take(MaxTrackedCallers / 4).Select(e => e.Key))
                _entries.TryRemove(key, out _);
    }
}
