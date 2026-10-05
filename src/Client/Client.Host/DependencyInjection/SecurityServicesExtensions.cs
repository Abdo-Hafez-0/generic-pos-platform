using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Security;

namespace Client.Host.DependencyInjection;

/// <summary>
/// Registers the platform-level security services that EVERY host has: the process session, the capability catalog, authorization and
/// the security-event pipeline. Modules add their own pieces (capability providers, the permission provider, event listeners)
/// through their own hosting modules; this class knows no module.
/// </summary>
public static class SecurityServicesExtensions
{
    public static IServiceCollection AddPlatformSecurity(this IServiceCollection services)
    {
        // One session per process (a desktop installation has one operator at a time).
        services.TryAddSingleton<SessionContext>();
        services.TryAddSingleton<ICurrentUser>(sp => sp.GetRequiredService<SessionContext>());
        services.TryAddSingleton<ISessionManager>(sp => sp.GetRequiredService<SessionContext>());

        // Modules declare what their operations check; the catalog is built once from whatever is installed.
        services.TryAddSingleton<ICapabilityCatalog>(sp => new CapabilityCatalog(sp.GetServices<ICapabilityProvider>()));

        // Scoped: the permission source (Users) reads the database through a scoped DbContext.
        services.TryAddScoped<IAuthorizationService, AuthorizationService>();

        services.TryAddSingleton<ISecurityEventSink, SecurityEventDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecurityEventListener, LoggingSecurityEventListener>());
        return services;
    }
}

/// <summary>Always-on listener: security events also go to the application log (never the secrets: events cannot carry any).</summary>
internal sealed class LoggingSecurityEventListener(ILogger<LoggingSecurityEventListener> logger) : ISecurityEventListener
{
    public Task OnEventAsync(SecurityEvent e, CancellationToken cancellationToken = default)
    {
        var level = e.Outcome == SecurityEventOutcome.Success ? LogLevel.Information : LogLevel.Warning;
        logger.Log(level, "Security event {Action} [{Outcome}] actor={Actor} subject={SubjectType}:{SubjectId} {Summary}",
            e.Action, e.Outcome, e.ActorName ?? e.ActorId?.ToString() ?? "-", e.SubjectType ?? "-", e.SubjectId ?? "-", e.Summary);
        return Task.CompletedTask;
    }
}
