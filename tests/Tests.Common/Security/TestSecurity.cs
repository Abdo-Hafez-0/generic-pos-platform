using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Security;
using Platform.Core.Results;

namespace Tests.Common.Security;

/// <summary>A clock tests move by hand.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset now) => _now = now;
}

/// <summary>Authorizes everything: for tests of business behaviour that are not about security. Never use it in production code.</summary>
public sealed class AllowAllAuthorizationService : IAuthorizationService
{
    public Task<Result> AuthorizeAsync(string capability, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());

    public Task<bool> IsAllowedAsync(string capability, CancellationToken cancellationToken = default) => Task.FromResult(true);
}

/// <summary>Authorizes exactly the capabilities it is given (and remembers what was asked), for tests of the authorization boundary.</summary>
public sealed class ScriptedAuthorizationService(params string[] allowed) : IAuthorizationService
{
    public HashSet<string> Allowed { get; } = new(allowed, StringComparer.OrdinalIgnoreCase);

    public List<string> Asked { get; } = [];

    public Task<Result> AuthorizeAsync(string capability, CancellationToken cancellationToken = default)
    {
        Asked.Add(capability);
        return Task.FromResult(Allowed.Contains(capability) ? Result.Success() : SecurityErrors.Forbidden(capability));
    }

    public Task<bool> IsAllowedAsync(string capability, CancellationToken cancellationToken = default) => Task.FromResult(Allowed.Contains(capability));
}

/// <summary>Keeps every security event so tests can assert what was audited (and what was not).</summary>
public sealed class RecordingSecurityEventSink : ISecurityEventSink
{
    public List<SecurityEvent> Events { get; } = [];

    public Task RecordAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
    {
        lock (Events) Events.Add(securityEvent);
        return Task.CompletedTask;
    }

    /// <summary>Every field of every event as one string, for "no secret anywhere" assertions.</summary>
    public string Dump() => string.Join("\n", Events.Select(e =>
        $"{e.Action}|{e.Outcome}|{e.ActorId}|{e.ActorName}|{e.SubjectType}|{e.SubjectId}|{e.Summary}"));
}
