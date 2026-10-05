using System.Diagnostics;

namespace Client.Licensing.Application;

public enum ClockCheck
{
    Ok = 0,

    /// <summary>The system clock is earlier than the last time this installation (provably) ran, or fell behind real elapsed time.</summary>
    RolledBack = 1
}

/// <summary>Where the clock high-water mark is kept (protected at rest when the platform can).</summary>
public interface IClockStateStore
{
    /// <summary>Null when nothing usable is stored (never written, deleted, or altered so that it no longer opens).</summary>
    Task<DateTimeOffset?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DateTimeOffset highWater, CancellationToken cancellationToken = default);
}

/// <summary>
/// Detects the system clock being turned back to stretch a license. See <see cref="ClockRollbackGuard"/> for what it can and cannot do.
/// </summary>
public interface IClockGuard
{
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Raises the lower bound the clock may not fall behind (a fact signed by the license server, e.g. when a license was issued). Never lowers it.</summary>
    void SetFloor(DateTimeOffset signedTime);

    /// <summary>Records the current wall-clock time and says whether it can be believed.</summary>
    ClockCheck Observe(DateTimeOffset wallNow);

    /// <summary>
    /// Sets the mark to a time the license SERVER vouched for (a freshly verified issuance), even if that lowers it. This is the recovery
    /// path after a clock that ran far ahead was corrected; it cannot be abused to hide a rollback because the clock must still be near that time.
    /// </summary>
    void Rebase(DateTimeOffset serverTime);

    Task FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>How far the clock may disagree before it is treated as turned back.</summary>
/// <param name="Tolerance">Bounded to 5 minutes .. 7 days so a configuration value can neither make the guard useless nor lock people out for ever.</param>
public sealed record ClockGuardOptions(TimeSpan Tolerance)
{
    public static readonly TimeSpan MinimumTolerance = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumTolerance = TimeSpan.FromDays(7);
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromHours(2);

    public static ClockGuardOptions Default { get; } = new(DefaultTolerance);

    public ClockGuardOptions Normalized()
        => new(Tolerance < MinimumTolerance ? MinimumTolerance : Tolerance > MaximumTolerance ? MaximumTolerance : Tolerance);
}

/// <summary>
/// Detects clock rollback with two independent facts: (1) a persisted high-water mark of the latest time this installation observed
/// (never below the signed issue time of its license), and (2) inside one run, the monotonic stopwatch, which a clock change cannot touch.
/// The clock is "rolled back" when it is earlier than the mark by more than the tolerance, or when it advanced less than the stopwatch did
/// by more than the tolerance (a clock that was turned back or held still while the program ran).
///
/// WHAT THIS IS NOT: a secure time source. Someone who controls the machine can still defeat it in principle (for example by editing the
/// binary), and each restart can win back at most one tolerance. A forward jump cannot raise the mark by more than the real elapsed time
/// of the run, so an accidentally wrong clock does not poison it; the one remaining false-positive (a clock set far AHEAD across a restart
/// and then corrected) is cleared by the next successful online renewal, which re-bases the mark on server-signed time. True protection
/// against a hostile clock needs a trusted time source (the server), which an offline-first product only has at renewal.
/// </summary>
public sealed class ClockRollbackGuard : IClockGuard
{
    private readonly IClockStateStore _store;
    private readonly TimeSpan _tolerance;
    private readonly Func<long> _timestamp;
    private readonly long _frequency;
    private readonly object _gate = new();

    private DateTimeOffset? _highWater;
    private DateTimeOffset? _flushed;
    private bool _anchored;
    private DateTimeOffset _anchorWall;
    private long _anchorStamp;
    private long _lastFlushStamp;
    private int _flushing;

    public ClockRollbackGuard(IClockStateStore store, ClockGuardOptions options, Func<long>? timestamp = null, long? frequency = null)
    {
        _store = store;
        _tolerance = options.Normalized().Tolerance;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _frequency = frequency ?? Stopwatch.Frequency;
    }

    /// <summary>How often (in monotonic time) an advanced mark is written to disk.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(1);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _store.LoadAsync(cancellationToken);
        lock (_gate)
        {
            if (stored is { } value && (_highWater is null || value > _highWater)) _highWater = value;
            _flushed = _highWater;
            _anchored = false; // a new run measures elapsed time from its own first observation
        }
    }

    public void SetFloor(DateTimeOffset signedTime)
    {
        lock (_gate)
        {
            if (_highWater is null || signedTime > _highWater) _highWater = signedTime;
        }
    }

    public ClockCheck Observe(DateTimeOffset wallNow)
    {
        bool flushNow = false;
        ClockCheck result;
        lock (_gate)
        {
            var stamp = _timestamp();
            if (!_anchored)
            {
                _anchored = true;
                _anchorWall = wallNow;
                _anchorStamp = stamp;
                _lastFlushStamp = stamp;
            }

            var realElapsed = TimeSpan.FromSeconds((stamp - _anchorStamp) / (double)_frequency);
            var wallElapsed = wallNow - _anchorWall;

            var fellBehindThisRun = wallElapsed < realElapsed - _tolerance;
            var earlierThanBefore = _highWater is { } mark && wallNow < mark - _tolerance;
            if (fellBehindThisRun || earlierThanBefore)
            {
                result = ClockCheck.RolledBack; // the mark is never advanced from a clock that cannot be believed
            }
            else
            {
                // A forward jump cannot move the mark further than real time allows (plus the tolerance).
                var believable = _anchorWall + realElapsed + _tolerance;
                var candidate = wallNow < believable ? wallNow : believable;
                if (_highWater is null || candidate > _highWater) _highWater = candidate;
                result = ClockCheck.Ok;
            }

            if (_highWater is { } high && high != _flushed
                && TimeSpan.FromSeconds((stamp - _lastFlushStamp) / (double)_frequency) >= FlushInterval)
            {
                _lastFlushStamp = stamp;
                flushNow = true;
            }
        }

        if (flushNow) _ = FlushQuietlyAsync();
        return result;
    }

    public void Rebase(DateTimeOffset serverTime)
    {
        lock (_gate) _highWater = serverTime;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset? toSave;
        lock (_gate) toSave = _highWater != _flushed ? _highWater : null;

        if (toSave is not { } value) return;

        await _store.SaveAsync(value, cancellationToken);
        lock (_gate) _flushed = value;
    }

    private async Task FlushQuietlyAsync()
    {
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return;
        try
        {
            await FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Writing the mark is best effort: the next attempt (or shutdown) retries; an unwritable disk must never stop selling.
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }
    }
}
